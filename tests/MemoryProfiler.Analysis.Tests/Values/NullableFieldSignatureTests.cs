using System.Reflection;
using MemoryProfiler.Analysis.Values;
using Microsoft.Diagnostics.Runtime;
using Xunit;

namespace MemoryProfiler.Analysis.Tests.Values;

// Field signatures and tokens for the Nullable-metadata tests come from this
// assembly's own module image, keeping the tests hermetic: no dump is required to
// exercise the closed-generic signature parsing used by the Windows dump fallback.
internal static class NullableSignatureTarget
{
    internal static int? Limit = 12;
    internal static int? Missing = null;
    internal static decimal? Amount = 1.5m;
    internal static int Plain = 42;
}

public sealed class NullableFieldSignatureTests
{
    private static readonly byte[] ModuleImage =
        File.ReadAllBytes(typeof(NullableSignatureTarget).Assembly.Location);

    private static int FieldToken(string fieldName) =>
        typeof(NullableSignatureTarget)
            .GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic)!
            .MetadataToken & 0x00FFFFFF;

    [Fact]
    public void ResolvesClosedPrimitiveArgumentFromModuleImage()
    {
        var parsed = ClrMdHeapValueReader.TryParseNullableFieldSignature(
            ModuleImage,
            FieldToken(nameof(NullableSignatureTarget.Limit)),
            expectedFieldName: null,
            out var element,
            out var typeName,
            out var failure);

        Assert.True(parsed);
        Assert.Equal(ClrElementType.Int32, element);
        Assert.Equal("System.Int32", typeName);
        Assert.Null(failure);
    }

    [Fact]
    public void ParsesEveryNullableFieldInTheModuleImage()
    {
        var parsed = ClrMdHeapValueReader.TryParseNullableFieldSignature(
            ModuleImage,
            FieldToken(nameof(NullableSignatureTarget.Missing)),
            expectedFieldName: nameof(NullableSignatureTarget.Missing),
            out var element,
            out var typeName,
            out var failure);

        Assert.True(parsed);
        Assert.Equal(ClrElementType.Int32, element);
        Assert.Equal("System.Int32", typeName);
        Assert.Null(failure);
    }

    [Fact]
    public void RejectsSignatureWhenExpectedFieldNameDoesNotMatch()
    {
        var parsed = ClrMdHeapValueReader.TryParseNullableFieldSignature(
            ModuleImage,
            FieldToken(nameof(NullableSignatureTarget.Limit)),
            expectedFieldName: nameof(NullableSignatureTarget.Plain),
            out _,
            out _,
            out var failure);

        Assert.False(parsed);
        Assert.Equal("Nullable metadata field mismatch", failure);
    }

    [Fact]
    public void RejectsUnknownFieldToken()
    {
        var parsed = ClrMdHeapValueReader.TryParseNullableFieldSignature(
            ModuleImage,
            fieldToken: 0xFFFFFF,
            expectedFieldName: null,
            out _,
            out _,
            out var failure);

        Assert.False(parsed);
        Assert.StartsWith("Nullable metadata parse failed", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsNonPrimitiveGenericArgument()
    {
        var parsed = ClrMdHeapValueReader.TryParseNullableFieldSignature(
            ModuleImage,
            FieldToken(nameof(NullableSignatureTarget.Amount)),
            expectedFieldName: null,
            out _,
            out _,
            out var failure);

        Assert.False(parsed);
        Assert.StartsWith("Nullable argument code", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsImageWithoutMetadataRoot()
    {
        var parsed = ClrMdHeapValueReader.TryParseNullableFieldSignature(
            new byte[] { 1, 2, 3, 4 },
            FieldToken(nameof(NullableSignatureTarget.Limit)),
            expectedFieldName: null,
            out _,
            out _,
            out var failure);

        Assert.False(parsed);
        Assert.Equal("Nullable metadata root not found", failure);
    }
}
