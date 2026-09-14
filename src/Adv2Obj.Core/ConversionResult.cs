namespace Adv2Obj.Core;

public sealed record ConversionResult(
    string InputPath,
    string OutputPath,
    int VertexCount,
    int FaceCount,
    int RepairedVertexCount,
    IReadOnlyList<string> Warnings);
