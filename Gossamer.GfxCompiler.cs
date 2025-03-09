namespace Gossamer.GfxCompiler;

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

using static System.Console;

class GfxCompiler
{
    static int Main(string[] args)
    {
        Directory.SetCurrentDirectory(Path.GetDirectoryName(Assembly.GetExecutingAssembly()!.Location)!);

        if (args.Length == 0 || args.Length < 2 || args[0] == "-h" || args[0] == "--help" || args[0] == "/?" || args[0] == "/help")
        {
            WriteLine("Usage: Gossamer.GfxCompiler <input> <output>" +
                "\n" +
                "\n  <input>   Input .hlsl file or directory containing .hlsl files" +
                "\n  <output>  Output file" +
                "\n" +
                "\n  Example: Gossamer.GfxCompiler shaders/ shaders.spv");
            return 1;
        }

        string inputArg = Path.GetFullPath(args[0]);
        string outputArg = Path.GetFullPath(args[1]);

        return ProducePackageFile(CompileShaders(ParseInputFiles(CollectInputFiles(inputArg))), outputArg);
    }

    record ShaderDefinition(string Filename, string Name, string[] Stages, string[] EntryPoints);
    record ShaderBytecode(string Stage, string EntryPoint, byte[] Bytecode);
    record ShaderPipeline(string Name, ShaderBytecode[] Bytecodes);

    record ShaderStageJson(uint Stage, string EntryPoint, long Offset, long Size);
    record ShaderPipelineJson(string Name, ShaderStageJson[] Stages);
    record ShaderPackageJson(Dictionary<string, ShaderPipelineJson> Pipelines);

    static string StageNameToEntrypoint(string shaderStageName) => shaderStageName switch
    {
        "vertex" => "vertex",
        "pixel" => "fragment",
        "fragment" => "fragment",
        "compute" => "compute",
        "geometry" => "geometry",
        _ => shaderStageName,
    };

    static string StageNameToProfile(string shaderStageName) => shaderStageName switch
    {
        "vertex" => "vs_6_0",
        "pixel" => "ps_6_0",
        "fragment" => "ps_6_0",
        "compute" => "cs_6_0",
        "geometry" => "gs_6_0",
        _ => throw new NotImplementedException(),
    };

    static uint StageNameToFlag(string shaderStageName) => shaderStageName switch
    {
        // VkShaderStageFlagBits 
        "vertex" => 0x00000001,
        "geometry" => 0x00000008,
        "pixel" => 0x00000010,
        "fragment" => 0x00000010,
        "compute" => 0x00000020,
        _ => throw new NotImplementedException(),
    };

    static IEnumerable<string> CollectInputFiles(string inputDirectoryOrFile)
    {
        if (File.Exists(inputDirectoryOrFile))
        {
            yield return inputDirectoryOrFile;
        }
        else if (Directory.Exists(inputDirectoryOrFile))
        {
            foreach (string filename in Directory.EnumerateFiles(inputDirectoryOrFile, "*.hlsl", SearchOption.AllDirectories))
            {
                yield return filename;
            }
        }
    }

    static IEnumerable<ShaderDefinition> ParseInputFiles(IEnumerable<string> inputFilenames)
    {
        List<string> shaderStages = [];
        List<string> shaderStagesEntryPoints = [];

        foreach (string inputFilename in inputFilenames)
        {
            shaderStages.Clear();
            shaderStagesEntryPoints.Clear();

            // Open the input file for reading lines
            using StreamReader reader = new(inputFilename);

            bool ignoreThisFile = true;

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.TrimStart();
                if (!line.StartsWith("[shader"))
                {
                    continue;
                }

                // Scan until first double quote
                int doubleQuoteIndex = line.IndexOf('"');
                if (doubleQuoteIndex == -1)
                {
                    WriteLine($"Error: Invalid shader name in {inputFilename}");
                    ignoreThisFile = true;
                    break;
                }

                // Scan until second double quote
                int secondDoubleQuoteIndex = line.IndexOf('"', doubleQuoteIndex + 1);
                if (secondDoubleQuoteIndex == -1)
                {
                    WriteLine($"Error: Invalid shader name in {inputFilename}");
                    ignoreThisFile = true;
                    break;
                }

                string shaderStageName = line[(doubleQuoteIndex + 1)..secondDoubleQuoteIndex];

                shaderStages.Add(shaderStageName);

                line = reader.ReadLine();
                if (line == null)
                {
                    break;
                }

                // Scan until first space
                int spaceIndex = line.IndexOf(' ');
                if (spaceIndex == -1)
                {
                    WriteLine($"Error: Invalid shader entry point in {inputFilename}");
                    ignoreThisFile = true;
                    break;
                }

                // Scan until opening parenthesis
                int openingParenthesisIndex = line.IndexOf('(', spaceIndex + 1);
                if (openingParenthesisIndex == -1)
                {
                    WriteLine($"Error: Invalid shader entry point in {inputFilename}");
                    ignoreThisFile = true;
                    break;
                }

                string shaderEntrypointName = line[(spaceIndex + 1)..openingParenthesisIndex].Trim();
                if (string.IsNullOrEmpty(shaderEntrypointName))
                {
                    WriteLine($"Error: Invalid shader entry point in {inputFilename}");
                    ignoreThisFile = true;
                    break;
                }

                shaderStagesEntryPoints.Add(shaderEntrypointName);
                ignoreThisFile = false;
            }

            if (ignoreThisFile)
            {
                continue;
            }

            // If no shader stages were found, print error and continue to the next file
            if (shaderStages.Count == 0)
            {
                WriteLine($"Error: No shader stages specified in {inputFilename}");
                continue;
            }

            // If shader stages entry points were found, validate the number of entry points
            if (shaderStagesEntryPoints.Count > 0 && shaderStagesEntryPoints.Count != shaderStages.Count)
            {
                WriteLine($"Error: Number of shader stage entry points does not match the number of shader stages in {inputFilename}");
                continue;
            }

            // If no shader stage entry points were found, use default entry points
            if (shaderStagesEntryPoints.Count == 0)
            {
                shaderStagesEntryPoints = shaderStages.Select(StageNameToEntrypoint).ToList();
            }

            yield return new ShaderDefinition(inputFilename, Path.GetFileNameWithoutExtension(inputFilename), [.. shaderStages], [.. shaderStagesEntryPoints]);
        }
    }

    static IEnumerable<ShaderPipeline> CompileShaders(IEnumerable<ShaderDefinition> shaderDefinitions)
    {
        string dxcOutputFile = Path.GetTempFileName();

        using Process dxcProcess = new();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            dxcProcess.StartInfo.FileName = "External/dxc.exe";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            dxcProcess.StartInfo.FileName = "External/dxc";
        }
        else
        {
            WriteLine("Error: Unsupported operating system");
            yield break;
        }

        List<ShaderBytecode> shaderBytecodes = [];

        foreach (ShaderDefinition shaderDefinition in shaderDefinitions)
        {
            shaderBytecodes.Clear();

            bool errorWhileCompiling = false;

            foreach ((string stageName, string stageEntryPoint) in shaderDefinition.Stages.Zip(shaderDefinition.EntryPoints))
            {
                string stageProfile = StageNameToProfile(stageName);

                dxcProcess.StartInfo.Arguments = $"-T {stageProfile} -E {stageEntryPoint} -WX -spirv -fspv-reflect -fspv-target-env=vulkan1.3 -Fo \"{dxcOutputFile}\" \"{shaderDefinition.Filename}\"";
                dxcProcess.Start();
                dxcProcess.WaitForExit();

                if (dxcProcess.ExitCode != 0)
                {
                    //WriteLine($"Error: Failed to compile {shaderDefinition.Filename}");
                    errorWhileCompiling = true;
                    break;
                }

                // Read the output file and add the shader bytecode to the list
                byte[] shaderBytecode = File.ReadAllBytes(dxcOutputFile);
                shaderBytecodes.Add(new ShaderBytecode(stageName, stageEntryPoint, shaderBytecode));

                // Clear the output file
                File.WriteAllText(dxcOutputFile, string.Empty);
            }

            if (errorWhileCompiling)
            {
                break;
            }

            WriteLine($"Program -> {shaderDefinition.Filename} {shaderDefinition.Name}[{string.Join(", ", shaderDefinition.Stages)}]");
            yield return new ShaderPipeline(shaderDefinition.Name, [.. shaderBytecodes]);
        }

        File.Delete(dxcOutputFile);
    }

    static int ProducePackageFile(IEnumerable<ShaderPipeline> shaderPipelines, string path)
    {
        byte[] binaryChunk = [];

        // Transform the sequence of ShaderPipeline objects into a JSON representation
        // and combine all shader bytecode into a single binary chunk
        ShaderPackageJson shaderPackageJson = new(shaderPipelines.Select(shaderPipeline =>
        {
            ShaderStageJson[] shaderStagesJson = shaderPipeline.Bytecodes.Select(shaderBytecode =>
            {
                int bytecodeLength = shaderBytecode.Bytecode.Length;

                // Copy the shader bytecode to the binary chunk
                Array.Resize(ref binaryChunk, binaryChunk.Length + bytecodeLength);
                shaderBytecode.Bytecode.CopyTo(binaryChunk, binaryChunk.Length - bytecodeLength);

                return new ShaderStageJson(
                    Stage: StageNameToFlag(shaderBytecode.Stage),
                    EntryPoint: shaderBytecode.EntryPoint,
                    Offset: binaryChunk.Length,
                    Size: bytecodeLength);
            }).ToArray();

            return new ShaderPipelineJson(shaderPipeline.Name, shaderStagesJson);
        }).ToDictionary(shaderPipeline => shaderPipeline.Name, shaderPipeline => shaderPipeline));

        // Is this a valid package?
        if (shaderPackageJson.Pipelines.Count == 0)
        {
            Write($"Error: No shader programs were compiled");
            return 1;
        }

        // Create the JSON chunk
        string jsonChunkString = JsonSerializer.Serialize(shaderPackageJson);
        byte[] jsonChunk = Encoding.UTF8.GetBytes(jsonChunkString);

        using FileStream outputStream = new(path, FileMode.OpenOrCreate);
        using BinaryWriter outputWriter = new(outputStream);

        // Write JSON chunk
        outputWriter.Write((uint)jsonChunk.Length);
        outputWriter.Write((uint)1);
        outputWriter.Write(jsonChunk);

        // Write binary chunk
        outputWriter.Write((uint)binaryChunk.Length);
        outputWriter.Write((uint)2);
        outputWriter.Write(binaryChunk);

        // What we produced
        Write($"Package -> {path}");
        return 0;
    }
}

