using System.Buffers;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Corvus.Text.Json.RuntimeEvaluator;
using JsonSchema = Corvus.Text.Json.Validator.JsonSchema;

// Bowtie's benchmark (`bowtie perf`) times each `run` command's round trip through the harness, from the
// moment the case is written to stdin until the result line is read back, in a fresh container per
// measurement; `start` and `dialect` are outside the timed region. The harness therefore keeps the `run`
// path to the work Bowtie is measuring (parse the case, compile the schema, validate the instances,
// write the result) and does its one-time initialisation in `start` and `dialect`.
ICommandSource cmdSource = args.Length == 0 ? new ConsoleCommandSource() : new FileCommandSource(args[0]);
using Stream stdout = Console.OpenStandardOutput();
ArrayBufferWriter<byte> response = new(4096);

bool started = false;

var unsupportedTests = new Dictionary<(string, string), string> {};

// The dialect applied to schemas that do not declare `$schema`, and whether `format` is asserted for it
// (the older drafts assert by default; 2019-09 and 2020-12 annotate unless the format-assertion vocabulary is on).
var dialects = new Dictionary<string, (JsonSchemaDialect Dialect, bool AssertFormat)> {
    ["https://json-schema.org/draft/2020-12/schema"] = (JsonSchemaDialect.Draft202012, false),
    ["https://json-schema.org/draft/2019-09/schema"] = (JsonSchemaDialect.Draft201909, false),
    ["http://json-schema.org/draft-07/schema#"] = (JsonSchemaDialect.Draft7, true),
    ["http://json-schema.org/draft-06/schema#"] = (JsonSchemaDialect.Draft6, true),
    ["http://json-schema.org/draft-04/schema#"] = (JsonSchemaDialect.Draft4, true),
};

JsonSchemaDialect? defaultDialect = null;
bool validateFormat = false;

// Bowtie (e.g. `bowtie site collect`) may drive several dialects through a single harness process,
// sending a new `dialect` command for each and restarting `seq` at 1. The library keeps a
// process-wide cache of compiled schemas keyed on the schema URI, so the URI we assign to each
// incoming schema must be unique across dialect runs, not just within one.
int dialectRun = 0;

while (cmdSource.GetNextCommand() is { } line && line.Length > 0)
{
    using JsonDocument root = JsonDocument.Parse(line);
    JsonElement command = root.RootElement;

    string? cmd = command.TryGetProperty("cmd"u8, out JsonElement cmdElement) ? cmdElement.GetString() : throw new MissingCommand(line);
    switch (cmd)
    {
        case "start":
            if (!command.TryGetProperty("version"u8, out JsonElement version))
            {
                throw new MissingVersion(cmd);
            }

            if (version.GetInt32() != 1)
            {
                throw new UnknownVersion(version.GetRawText());
            }

            started = true;
            using (Utf8JsonWriter writer = BeginResponse())
            {
                writer.WriteStartObject();
                writer.WriteNumber("version"u8, 1);
                writer.WriteStartObject("implementation"u8);
                writer.WriteString("language"u8, "dotnet");
                writer.WriteString("name"u8, "dotnet-corvus-jsonschema-v5engine");
                writer.WriteString("version"u8, GetLibVersion());
                writer.WriteString("homepage"u8, "https://github.com/corvus-dotnet/corvus.jsonschema");
                writer.WriteString("documentation"u8, "https://github.com/corvus-dotnet/Corvus.JsonSchema/blob/main/README.md");
                writer.WriteString("issues"u8, "https://github.com/corvus-dotnet/corvus.jsonschema/issues");
                writer.WriteString("source"u8, "https://github.com/corvus-dotnet/corvus.jsonschema");
                writer.WriteStartArray("dialects"u8);
                foreach (string dialectUri in dialects.Keys)
                {
                    writer.WriteStringValue(dialectUri);
                }

                writer.WriteEndArray();
                writer.WriteString("os"u8, Environment.OSVersion.Platform.ToString());
                writer.WriteString("os_version"u8, Environment.OSVersion.Version.ToString());
                writer.WriteString("language_version"u8, Environment.Version.ToString());
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            EndResponse();
            break;

        case "dialect":
            if (!started)
            {
                throw new NotStarted();
            }

            string dialect = command.TryGetProperty("dialect"u8, out JsonElement dialectElement) ? dialectElement.GetString()! : throw new MissingDialect(line);
            (defaultDialect, validateFormat) = dialects[dialect];
            dialectRun++;

            // One-time work for this dialect, outside Bowtie's timed region: load and compile the dialect's
            // metaschema, and take the evaluator through its first schema compilation, pattern, format and
            // evaluation, so that the first timed `run` pays only for its own case.
            WarmUp(dialect, defaultDialect.Value, validateFormat, dialectRun);

            using (Utf8JsonWriter writer = BeginResponse())
            {
                writer.WriteStartObject();
                writer.WriteBoolean("ok"u8, true);
                writer.WriteEndObject();
            }

            EndResponse();
            break;

        case "run":
            if (!started)
            {
                throw new NotStarted();
            }

            if (!command.TryGetProperty("case"u8, out JsonElement testCase))
            {
                throw new MissingCase(line);
            }

            JsonElement seq = command.TryGetProperty("seq"u8, out JsonElement seqElement) ? seqElement : default;

            if (!testCase.TryGetProperty("description"u8, out JsonElement caseDescription) || caseDescription.ValueKind != JsonValueKind.String)
            {
                throw new MissingTestCaseDescription(testCase.GetRawText());
            }

            string testCaseDescription = caseDescription.GetString()!;

            if (!testCase.TryGetProperty("schema"u8, out JsonElement schemaElement))
            {
                throw new MissingSchema(testCase.GetRawText());
            }

            if (defaultDialect is not JsonSchemaDialect dialectForRun)
            {
                throw new CannotRunBeforeDialectIsChosen();
            }

            // The registry supplies the documents that `$ref`s in the test case's schema may reach, keyed by
            // absolute URI; the evaluator asks the resolver for a document by that URI (without a fragment).
            Dictionary<string, byte[]> registryDocuments = new(StringComparer.Ordinal);

            if (testCase.TryGetProperty("registry"u8, out JsonElement registry) && registry.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty entry in registry.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Null)
                    {
                        registryDocuments[entry.Name] = JsonMarshal.GetRawUtf8Value(entry.Value).ToArray();
                    }
                }
            }

            JsonSchemaDocumentResolver resolver = (string uri, out ReadOnlyMemory<byte> utf8Json) =>
            {
                if (registryDocuments.TryGetValue(uri, out byte[]? utf8))
                {
                    utf8Json = utf8;
                    return true;
                }

                utf8Json = default;
                return false;
            };

            string fakeURI = $"https://example.com/bowtie-sent-schema-{dialectRun}-{seq.GetRawText()}.json";

            string testDescription = string.Empty;

            if (!testCase.TryGetProperty("tests"u8, out JsonElement tests) || tests.ValueKind != JsonValueKind.Array)
            {
                throw new MissingTests(testCase.GetRawText());
            }

            try
            {
                // The schema's raw UTF-8 text goes to the validator as-is: no JSON object model is built and
                // nothing is re-serialised in between.
                ReadOnlyMemory<byte> schemaUtf8 = RawSlice(line, schemaElement);
                var schema = JsonSchema.FromStream(new MemoryStream(schemaUtf8.ToArray(), writable: false), fakeURI,
                                                   new JsonSchema.Options(additionalDocumentResolver: resolver,
                                                                          defaultDialect: dialectForRun,
                                                                          alwaysAssertFormat: validateFormat,
                                                                          allowFileSystemAndHttpResolution: false),
                                                   refreshCache: true);

                using (Utf8JsonWriter writer = BeginResponse())
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("seq"u8);
                    seq.WriteTo(writer);
                    writer.WriteStartArray("results"u8);

                    foreach (JsonElement test in tests.EnumerateArray())
                    {
                        if (!test.TryGetProperty("description"u8, out JsonElement description) || description.ValueKind != JsonValueKind.String)
                        {
                            throw new MissingTestDescription(test.GetRawText());
                        }

                        testDescription = description.GetString()!;

                        // The instance is validated straight from its raw UTF-8 slice of the command line.
                        ReadOnlyMemory<byte> instance = test.TryGetProperty("instance"u8, out JsonElement instanceElement)
                            ? RawSlice(line, instanceElement)
                            : "null"u8.ToArray();
                        bool validationResult = schema.Validate(instance);
                        writer.WriteStartObject();
                        writer.WriteBoolean("valid"u8, validationResult);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                EndResponse();
            }
            catch (Exception)
                when (unsupportedTests.TryGetValue((testCaseDescription, testDescription), out string? message))
            {
                response.Clear();
                using (Utf8JsonWriter writer = BeginResponse())
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("seq"u8);
                    seq.WriteTo(writer);
                    writer.WriteBoolean("skipped"u8, true);
                    writer.WriteString("message"u8, message);
                    writer.WriteEndObject();
                }

                EndResponse();
            }
            catch (Exception e)
            {
                response.Clear();
                using (Utf8JsonWriter writer = BeginResponse())
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("seq"u8);
                    seq.WriteTo(writer);
                    writer.WriteBoolean("errored"u8, true);
                    writer.WriteStartObject("context"u8);
                    writer.WriteString("message"u8, e.ToString());
                    writer.WriteString("traceback"u8, Environment.StackTrace);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                EndResponse();
            }

            break;

        case "stop":
            if (!started)
            {
                throw new NotStarted();
            }

            Environment.Exit(0);
            break;

        case null:
            throw new UnknownCommand("Missing command!");

        default:
            throw new UnknownCommand(cmd);
    }
}

Utf8JsonWriter BeginResponse()
{
    response.Clear();
    return new Utf8JsonWriter(response);
}

void EndResponse()
{
    stdout.Write(response.WrittenSpan);
    stdout.WriteByte((byte)'\n');
    stdout.Flush();
}

// The raw UTF-8 text of an element, as a slice of the command line it was parsed from (JsonDocument does not copy
// the memory it is given), copied only if the element does not point into that memory.
static ReadOnlyMemory<byte> RawSlice(ReadOnlyMemory<byte> line, JsonElement element)
{
    ReadOnlySpan<byte> raw = JsonMarshal.GetRawUtf8Value(element);
    ReadOnlySpan<byte> whole = line.Span;
    nint offset = Unsafe.ByteOffset(ref MemoryMarshal.GetReference(whole), ref MemoryMarshal.GetReference(raw));

    if (offset >= 0 && offset + raw.Length <= whole.Length)
    {
        return line.Slice((int)offset, raw.Length);
    }

    return raw.ToArray();
}

static void WarmUp(string dialectUri, JsonSchemaDialect dialect, bool assertFormat, int run)
{
    try
    {
        string schemaText = $$"""
            {
              "$schema": "{{dialectUri}}",
              "allOf": [{ "$ref": "{{dialectUri}}" }],
              "properties": {
                "a": { "type": "string", "format": "email", "pattern": "^[a-z]+@[a-z]+\\.[a-z]+$", "minLength": 3 },
                "b": { "type": "array", "items": { "type": "integer", "minimum": 0 }, "uniqueItems": true },
                "c": { "enum": ["x", "y", 1, null] },
                "d": { "type": "object", "additionalProperties": { "type": "number" } }
              },
              "required": ["a"]
            }
            """;
        var schema = JsonSchema.FromText(schemaText, $"https://example.com/bowtie-warm-up-{run}.json",
                                         new JsonSchema.Options(defaultDialect: dialect,
                                                                alwaysAssertFormat: assertFormat,
                                                                allowFileSystemAndHttpResolution: false),
                                         refreshCache: true);
        schema.Validate("""{"a":"me@example.com","b":[1,2,3],"c":"x","d":{"n":1.5}}""");
        schema.Validate("""{"a":"nope","b":[1,1],"c":"z","d":{"n":"s"}}""");
        schema.Validate("""{"b":[]}""");
    }
    catch (Exception)
    {
        // Warm-up is best-effort; a failure here shows up in the run that follows.
    }
}

static string GetLibVersion()
{
    AssemblyInformationalVersionAttribute? attribute =
        typeof(JsonSchema).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
#pragma warning disable SYSLIB1045 // Convert to 'GeneratedRegexAttribute'.
    return Regex.Match(attribute!.InformationalVersion, @"\d+\.\d+\.\d+").Value;
#pragma warning restore SYSLIB1045 // Convert to 'GeneratedRegexAttribute'.
}

internal interface ICommandSource
{
    /// <summary>Gets the next command line as UTF-8 (without the line terminator), or null at end of input.</summary>
    ReadOnlyMemory<byte>? GetNextCommand();
}

internal class MissingCommand(ReadOnlyMemory<byte> root) : Exception
{
    public string Root { get; } = Encoding.UTF8.GetString(root.Span);
}

internal class MissingTest(string tests) : Exception
{
    public string Tests { get; } = tests;
}

internal class MissingCase(ReadOnlyMemory<byte> root) : Exception
{
    public string Root { get; } = Encoding.UTF8.GetString(root.Span);
}

internal class MissingSchema(string testCase) : Exception
{
    public string TestCase { get; } = testCase;
}

internal class MissingTestDescription(string testInstance) : Exception
{
    public string TestInstance { get; } = testInstance;
}

internal class MissingDialect(ReadOnlyMemory<byte> root) : Exception
{
    public string Root { get; } = Encoding.UTF8.GetString(root.Span);
}

internal class MissingTestCaseDescription(string testCase) : Exception
{
    public string TestCase { get; } = testCase;
}

internal class MissingTests(string testCase) : Exception
{
    public string TestCase { get; } = testCase;
}

internal class UnknownCommand(string? message) : Exception
(message) { }

internal class MissingVersion(string command) : Exception
{
    public string Command { get; } = command;
}

internal class UnknownVersion(string version) : Exception
{
    public string Version { get; } = version;
}

internal class NotStarted : Exception;

internal class CannotRunBeforeDialectIsChosen : Exception;

/// <summary>Reads newline-delimited commands from standard input as UTF-8 bytes.</summary>
internal class ConsoleCommandSource : ICommandSource
{
    private readonly Stream stdin = Console.OpenStandardInput();
    private byte[] buffer = new byte[64 * 1024];
    private int start;
    private int end;

    public ReadOnlyMemory<byte>? GetNextCommand()
    {
        while (true)
        {
            int newline = Array.IndexOf(this.buffer, (byte)'\n', this.start, this.end - this.start);
            if (newline >= 0)
            {
                ReadOnlyMemory<byte> lineBytes = new(this.buffer, this.start, newline - this.start);
                this.start = newline + 1;
                return TrimCarriageReturn(lineBytes);
            }

            if (this.start > 0)
            {
                Array.Copy(this.buffer, this.start, this.buffer, 0, this.end - this.start);
                this.end -= this.start;
                this.start = 0;
            }

            if (this.end == this.buffer.Length)
            {
                Array.Resize(ref this.buffer, this.buffer.Length * 2);
            }

            int read = this.stdin.Read(this.buffer, this.end, this.buffer.Length - this.end);
            if (read == 0)
            {
                if (this.end > this.start)
                {
                    ReadOnlyMemory<byte> lineBytes = new(this.buffer, this.start, this.end - this.start);
                    this.start = this.end;
                    return TrimCarriageReturn(lineBytes);
                }

                return null;
            }

            this.end += read;
        }
    }

    private static ReadOnlyMemory<byte> TrimCarriageReturn(ReadOnlyMemory<byte> lineBytes)
    {
        return lineBytes.Length > 0 && lineBytes.Span[^1] == (byte)'\r' ? lineBytes[..^1] : lineBytes;
    }
}

/// <summary>Reads newline-delimited commands from a file (used when a file name is given on the command line).</summary>
internal class FileCommandSource(string fileName) : ICommandSource
{
    private readonly byte[] fileContents = File.ReadAllBytes(fileName);
    private int position;

    public ReadOnlyMemory<byte>? GetNextCommand()
    {
        if (this.position >= this.fileContents.Length)
        {
            return null;
        }

        int newline = Array.IndexOf(this.fileContents, (byte)'\n', this.position);
        int lineEnd = newline < 0 ? this.fileContents.Length : newline;
        ReadOnlyMemory<byte> lineBytes = new(this.fileContents, this.position, lineEnd - this.position);
        this.position = lineEnd + 1;
        return lineBytes.Length > 0 && lineBytes.Span[^1] == (byte)'\r' ? lineBytes[..^1] : lineBytes;
    }
}
