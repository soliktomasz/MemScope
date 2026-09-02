using System.Collections.Immutable;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Microsoft.Diagnostics.Runtime;
using MemoryProfiler.Analysis.Objects;
using MemoryProfiler.Contracts.Heap;

namespace MemoryProfiler.Analysis.Values;

internal static class ClrMdHeapValueReader
{
    public static HeapObjectValueResult Read(
        ClrRuntime runtime,
        ulong objectAddress,
        ObjectValueReadOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var heap = runtime.Heap;
        var obj = heap.GetObject(objectAddress);
        if (obj.IsNull || !obj.IsValid || obj.IsFree ||
            obj.Type is null || obj.Type.MethodTable == 0)
        {
            throw new InvalidDataException(
                "The object cannot be inspected from the heap.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var header = new HeapObjectInfo(
            obj.Address,
            obj.Type.MethodTable,
            obj.Type.Name ?? string.Empty,
            obj.Size,
            ClrMdHeapObjectRepository.GenerationLabel(
                heap.GetSegmentByAddress(obj.Address)?.GetGeneration(obj.Address)));

        if (obj.IsArray)
        {
            return ReadArray(heap, obj, header, options, cancellationToken);
        }

        return ReadObjectFields(heap, obj, header, options, cancellationToken);
    }

    private static HeapObjectValueResult ReadObjectFields(
        ClrHeap heap,
        ClrObject obj,
        HeapObjectInfo header,
        ObjectValueReadOptions options,
        CancellationToken cancellationToken)
    {
        var fields = new List<HeapFieldValue>();
        foreach (var field in obj.Type!.Fields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fields.Add(ReadField(obj, field, options, cancellationToken));
        }

        return new HeapObjectValueResult(header, fields, fields.Count, HasMoreElements: false);
    }

    private static HeapObjectValueResult ReadArray(
        ClrHeap heap,
        ClrObject obj,
        HeapObjectInfo header,
        ObjectValueReadOptions options,
        CancellationToken cancellationToken)
    {
        var array = obj.AsArray();
        var length = array.Length;
        var start = options.ArrayOffset;
        var limit = options.ArrayLimit;
        var count = Math.Min(limit, Math.Max(0, length - start));
        var componentType = array.Type?.ComponentType;
        var elementType = componentType?.ElementType ?? ClrElementType.Unknown;
        var isReference =
            componentType?.IsObjectReference == true ||
            elementType is ClrElementType.Class or
                ClrElementType.Object or
                ClrElementType.Array or
                ClrElementType.SZArray;

        var fields = new List<HeapFieldValue>(count);
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fields.Add(
                ReadArrayElement(
                    array,
                    start + index,
                    elementType,
                    componentType?.Name ?? string.Empty,
                    isReference,
                    cancellationToken));
        }

        var hasMore = start + count < length;
        return new HeapObjectValueResult(header, fields, length, hasMore);
    }

    private static HeapFieldValue ReadArrayElement(
        ClrArray array,
        int index,
        ClrElementType elementType,
        string declaredTypeName,
        bool isReference,
        CancellationToken cancellationToken)
    {
        var name = $"[{index}]";
        if (isReference)
        {
            return ArrayElementReference(name, declaredTypeName, array.GetObjectValue(index));
        }

        return elementType switch
        {
            ClrElementType.Boolean => ArrayElementScalar(name, declaredTypeName, array.GetValue<bool>(index)),
            ClrElementType.Char => ArrayElementScalar(name, declaredTypeName, HeapValueFormatting.Character(array.GetValue<char>(index))),
            ClrElementType.Int8 => ArrayElementScalar(name, declaredTypeName, array.GetValue<sbyte>(index)),
            ClrElementType.UInt8 => ArrayElementScalar(name, declaredTypeName, array.GetValue<byte>(index)),
            ClrElementType.Int16 => ArrayElementScalar(name, declaredTypeName, array.GetValue<short>(index)),
            ClrElementType.UInt16 => ArrayElementScalar(name, declaredTypeName, array.GetValue<ushort>(index)),
            ClrElementType.Int32 => ArrayElementScalar(name, declaredTypeName, array.GetValue<int>(index)),
            ClrElementType.UInt32 => ArrayElementScalar(name, declaredTypeName, array.GetValue<uint>(index)),
            ClrElementType.Int64 => ArrayElementScalar(name, declaredTypeName, array.GetValue<long>(index)),
            ClrElementType.UInt64 => ArrayElementScalar(name, declaredTypeName, array.GetValue<ulong>(index)),
            ClrElementType.Float => ArrayElementScalar(name, declaredTypeName, array.GetValue<float>(index)),
            ClrElementType.Double => ArrayElementScalar(name, declaredTypeName, array.GetValue<double>(index)),
            _ => ArrayElementUnavailable(name, declaredTypeName, "Unsupported value type"),
        };
    }

    private static HeapFieldValue ReadField(
        ClrObject obj,
        ClrInstanceField field,
        ObjectValueReadOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            if (field.Type?.BaseType?.Name == "System.Enum")
            {
                return ReadEnum(field, obj);
            }

            if (IsStringField(field))
            {
                return String(field, obj, options.StringLimit);
            }

            return field.ElementType switch
            {
                ClrElementType.Boolean => Primitive(field, obj.ReadField<bool>(field)),
                ClrElementType.Char => Character(field, obj.ReadField<char>(field)),
                ClrElementType.Int8 => Primitive(field, obj.ReadField<sbyte>(field)),
                ClrElementType.UInt8 => Primitive(field, obj.ReadField<byte>(field)),
                ClrElementType.Int16 => Primitive(field, obj.ReadField<short>(field)),
                ClrElementType.UInt16 => Primitive(field, obj.ReadField<ushort>(field)),
                ClrElementType.Int32 => PrimitiveOrEnum(field, obj.ReadField<int>(field)),
                ClrElementType.UInt32 => PrimitiveOrEnum(field, obj.ReadField<uint>(field)),
                ClrElementType.Int64 => PrimitiveOrEnum(field, obj.ReadField<long>(field)),
                ClrElementType.UInt64 => PrimitiveOrEnum(field, obj.ReadField<ulong>(field)),
                ClrElementType.Float => Primitive(field, obj.ReadField<float>(field)),
                ClrElementType.Double => Primitive(field, obj.ReadField<double>(field)),
                ClrElementType.String => String(field, obj, options.StringLimit),
                ClrElementType.Class or ClrElementType.Object or
                    ClrElementType.Array or ClrElementType.SZArray => ReferenceField(obj, field),
                ClrElementType.Struct => WellKnownStructOrUnavailable(field, obj, options, cancellationToken),
                _ => Unavailable(field, "Unsupported value type"),
            };
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                InvalidOperationException or
                IOException or
                ClrDiagnosticsException)
        {
            return Unavailable(field, "Value could not be read");
        }
    }

    // Windows minidumps can report string-typed instance fields as plain reference
    // slots (ElementType Class or Object) while still resolving the declared type
    // name; full dumps report ElementType.String. Decode both shapes as strings.
    private static bool IsStringField(ClrInstanceField field) =>
        field.ElementType is ClrElementType.String or ClrElementType.Class or ClrElementType.Object &&
        string.Equals(field.Type?.Name, "System.String", StringComparison.Ordinal);

    private static HeapFieldValue ReadEnum(ClrInstanceField field, ClrObject obj)
    {
        var enumType = field.Type!;
        var elementType = enumType.AsEnum().ElementType;
        var value = ReadObjectPrimitive(obj, field, elementType);
        var numeric = Invariant(value);
        var name = EnumName(enumType, value);
        var text = name is null ? numeric : $"{name} ({numeric})";
        return Enum(field, text);
    }

    private static object ReadObjectPrimitive(ClrObject obj, ClrInstanceField field, ClrElementType elementType) =>
        elementType switch
        {
            ClrElementType.Boolean => obj.ReadField<bool>(field),
            ClrElementType.Char => obj.ReadField<char>(field),
            ClrElementType.Int8 => obj.ReadField<sbyte>(field),
            ClrElementType.UInt8 => obj.ReadField<byte>(field),
            ClrElementType.Int16 => obj.ReadField<short>(field),
            ClrElementType.UInt16 => obj.ReadField<ushort>(field),
            ClrElementType.Int32 => obj.ReadField<int>(field),
            ClrElementType.UInt32 => obj.ReadField<uint>(field),
            ClrElementType.Int64 => obj.ReadField<long>(field),
            ClrElementType.UInt64 => obj.ReadField<ulong>(field),
            ClrElementType.Float => obj.ReadField<float>(field),
            ClrElementType.Double => obj.ReadField<double>(field),
            _ => throw new InvalidOperationException("Unsupported enum underlying type."),
        };

    private static HeapFieldValue PrimitiveOrEnum(ClrInstanceField field, object value)
    {
        if (field.Type?.BaseType?.Name == "System.Enum")
        {
            var numeric = Invariant(value);
            var name = EnumName(field.Type, value);
            var text = name is null ? numeric : $"{name} ({numeric})";
            return Enum(field, text);
        }

        return Primitive(field, value);
    }

    private static HeapFieldValue WellKnownStructOrUnavailable(
        ClrInstanceField field,
        ClrObject obj,
        ObjectValueReadOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            if (field.Type?.BaseType?.Name == "System.Enum")
            {
                return ReadEnum(field, obj);
            }

            var vt = obj.ReadValueTypeField(field);
            if (!vt.IsValid)
            {
                return Unavailable(field, "Unsupported value type");
            }

            return DecodeValueType(field, vt, obj, options, cancellationToken);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                InvalidOperationException or
                IOException or
                ClrDiagnosticsException)
        {
            return Unavailable(field, "Value could not be read");
        }
    }

    private static HeapFieldValue DecodeValueType(
        ClrInstanceField field,
        ClrValueType vt,
        ClrObject obj,
        ObjectValueReadOptions options,
        CancellationToken cancellationToken)
    {
        var typeName = vt.Type?.Name;
        switch (typeName)
        {
            case "System.Decimal":
                return Primitive(field, ReadDecimal(vt));
            case "System.DateTime":
                return Primitive(field, ReadDateTime(vt));
            case "System.TimeSpan":
                return Primitive(field, ReadTimeSpan(vt));
            case "System.Guid":
                return Primitive(field, ReadGuid(vt));
        }

        if (vt.Type is not null && HasField(vt.Type, "hasValue"))
        {
            return DecodeNullable(field, vt, obj, options, cancellationToken);
        }

        return Unavailable(field, "Unsupported value type");
    }

    private static HeapFieldValue DecodeNullable(
        ClrInstanceField field,
        ClrValueType nullable,
        ClrObject obj,
        ObjectValueReadOptions options,
        CancellationToken cancellationToken)
    {
        var valueField = nullable.Type?.Fields
            .FirstOrDefault(candidate => candidate.Name == "value");

        if (IsDegenerateNullableLayout(valueField))
        {
            return DecodeNullableFromRawMemory(field, obj);
        }

        var hasValue = nullable.ReadField<bool>("hasValue");
        if (!hasValue)
        {
            return Null(field);
        }

        if (valueField is null)
        {
            return Unavailable(field, "Unsupported value type");
        }

        return DecodeValueTypeMember(field, nullable, valueField, obj, options, cancellationToken);
    }

    // Windows minidumps can make ClrMD resolve a Nullable<T> instance field as its
    // open generic definition: the "value" member is then a Class-typed placeholder
    // ("T") reported at offset 0 with the size of a reference, and hasValue is placed
    // after it. Reading through that ClrValueType therefore lands on the wrong bytes.
    // Detect the degenerate shape and decode the raw field slot instead; ClrMD's own
    // field addressing (GetAddress) keeps the slot base accurate even though the
    // value-type model is not.
    private static bool IsDegenerateNullableLayout(ClrInstanceField? valueField) =>
        valueField is not null &&
        valueField.ElementType == ClrElementType.Class &&
        string.Equals(valueField.Type?.Name, "T", StringComparison.Ordinal);

    private static HeapFieldValue DecodeNullableFromRawMemory(
        ClrInstanceField field,
        ClrObject obj)
    {
        var heap = obj.Type?.Heap;
        var reader = heap?.Runtime.DataTarget.DataReader;
        if (heap is null || reader is null)
        {
            return Unavailable(field, "Value could not be read");
        }

        // ClrMD field offsets are relative to the object data (past the method
        // table), so address the real slot through ClrMD instead of adding the raw
        // offset to the object address; reading at obj.Address + Offset lands one
        // pointer too early and decodes neighbouring bytes.
        ulong slotAddress;
        try
        {
            slotAddress = field.GetAddress(obj.Address, interior: false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                InvalidOperationException or
                IOException or
                ClrDiagnosticsException)
        {
            return Unavailable(field, "Value could not be read");
        }

        // Real Nullable<T> instance-field layout: bool hasValue occupies the first
        // byte of the slot regardless of how ClrMD models the open generic.
        if (!TryReadByte(reader, slotAddress, out var hasValue))
        {
            return Unavailable(field, "Value could not be read");
        }

        if (hasValue == 0)
        {
            return Null(field, field.Type?.Name ?? "System.Nullable<T>");
        }

        var module = field.ContainingType?.Module;
        if (module is null ||
            module.IsDynamic ||
            !module.IsPEFile)
        {
            return Unavailable(field, "Unsupported value type");
        }

        if (!TryResolveNullableUnderlying(
                field,
                module,
                reader,
                out var element,
                out var underlyingName,
                out var failure))
        {
            return Unavailable(field, failure ?? "Unsupported value type");
        }

        var valueOffset = NullableValueOffset(element);
        var size = SizeOf(element);
        var buffer = new byte[size];
        if (ReadExactly(reader, slotAddress + valueOffset, buffer) != size)
        {
            return Unavailable(field, "Value could not be read");
        }

        var value = ReadPrimitiveFromBytes(element, buffer);
        if (value is null)
        {
            return Unavailable(field, "Unsupported value type");
        }

        var declaredTypeName = $"System.Nullable<{underlyingName}>";
        return element switch
        {
            ClrElementType.Char => Field(
                field.Name,
                declaredTypeName,
                HeapValueKind.Primitive,
                HeapValueFormatting.Character((char)value),
                null,
                null,
                false,
                null,
                null),
            _ => Field(
                field.Name,
                declaredTypeName,
                HeapValueKind.Primitive,
                Invariant(value),
                null,
                null,
                false,
                null,
                null),
        };
    }

    // Maps a metadata element-type code back to the closest ClrElementType used for
    // formatting. Only primitive underlying types of Nullable<T> are supported here;
    // anything else (enums, structs, user types) is reported as unavailable.
    private static bool TryMapElementCode(int code, out ClrElementType element, out string typeName)
    {
        switch (code)
        {
            case 0x02: // ELEMENT_TYPE_BOOLEAN
                element = ClrElementType.Boolean;
                typeName = "System.Boolean";
                return true;
            case 0x03: // ELEMENT_TYPE_CHAR
                element = ClrElementType.Char;
                typeName = "System.Char";
                return true;
            case 0x04: // ELEMENT_TYPE_I1
                element = ClrElementType.Int8;
                typeName = "System.SByte";
                return true;
            case 0x05: // ELEMENT_TYPE_U1
                element = ClrElementType.UInt8;
                typeName = "System.Byte";
                return true;
            case 0x06: // ELEMENT_TYPE_I2
                element = ClrElementType.Int16;
                typeName = "System.Int16";
                return true;
            case 0x07: // ELEMENT_TYPE_U2
                element = ClrElementType.UInt16;
                typeName = "System.UInt16";
                return true;
            case 0x08: // ELEMENT_TYPE_I4
                element = ClrElementType.Int32;
                typeName = "System.Int32";
                return true;
            case 0x09: // ELEMENT_TYPE_U4
                element = ClrElementType.UInt32;
                typeName = "System.UInt32";
                return true;
            case 0x0A: // ELEMENT_TYPE_I8
                element = ClrElementType.Int64;
                typeName = "System.Int64";
                return true;
            case 0x0B: // ELEMENT_TYPE_U8
                element = ClrElementType.UInt64;
                typeName = "System.UInt64";
                return true;
            case 0x0C: // ELEMENT_TYPE_R4
                element = ClrElementType.Float;
                typeName = "System.Single";
                return true;
            case 0x0D: // ELEMENT_TYPE_R8
                element = ClrElementType.Double;
                typeName = "System.Double";
                return true;
            default:
                element = ClrElementType.Unknown;
                typeName = string.Empty;
                return false;
        }
    }

    private static uint NullableValueOffset(ClrElementType element) =>
        (uint)AlignUp(1, (int)SizeOf(element));

    private static uint SizeOf(ClrElementType element) =>
        element switch
        {
            ClrElementType.Boolean or ClrElementType.Int8 or ClrElementType.UInt8 => 1,
            ClrElementType.Char or ClrElementType.Int16 or ClrElementType.UInt16 => 2,
            ClrElementType.Int32 or ClrElementType.UInt32 or ClrElementType.Float => 4,
            ClrElementType.Int64 or ClrElementType.UInt64 or ClrElementType.Double => 8,
            _ => 0,
        };

    private static int AlignUp(int value, int alignment) =>
        (value + alignment - 1) / alignment * alignment;

    private static object? ReadPrimitiveFromBytes(ClrElementType element, byte[] buffer)
    {
        Span<byte> span = buffer;
        return element switch
        {
            ClrElementType.Boolean => span[0] != 0,
            ClrElementType.Char => (char)System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(span),
            ClrElementType.Int8 => unchecked((sbyte)span[0]),
            ClrElementType.UInt8 => span[0],
            ClrElementType.Int16 => System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(span),
            ClrElementType.UInt16 => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(span),
            ClrElementType.Int32 => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(span),
            ClrElementType.UInt32 => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span),
            ClrElementType.Int64 => System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(span),
            ClrElementType.UInt64 => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(span),
            ClrElementType.Float => System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(span),
            ClrElementType.Double => System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(span),
            _ => null,
        };
    }

    // Resolves the element type that T stands for in a Nullable<T> field whose
    // ClrMD type came back as the open generic definition. The instance field's
    // signature in the owning module's metadata carries the closed generic argument.
    // The captured process's metadata image is authoritative and used first; Windows
    // minidumps often omit module image memory, so the module file on disk is tried
    // as a fallback when it is still available (same-machine analysis).
    private static bool TryResolveNullableUnderlying(
        ClrInstanceField field,
        ClrModule module,
        object reader,
        out ClrElementType element,
        out string typeName,
        out string? failure)
    {
        element = ClrElementType.Unknown;
        typeName = string.Empty;
        failure = null;

        if (module.MetadataAddress != 0 &&
            module.MetadataLength is > 0 and <= 64 * 1024 * 1024)
        {
            var dumpImage = new byte[module.MetadataLength];
            if (ReadExactly(reader, module.MetadataAddress, dumpImage) != dumpImage.Length)
            {
                failure = "Nullable metadata read failed";
            }
            else if (TryParseNullableFieldSignature(
                field,
                dumpImage,
                verifyFieldName: false,
                out element,
                out typeName,
                out failure))
            {
                return true;
            }
        }

        if (TryResolveNullableUnderlyingFromModuleFile(
                field,
                module,
                out element,
                out typeName,
                out failure))
        {
            return true;
        }

        return false;
    }

    // Reads the owning module's metadata image from the file the module was loaded
    // from. Only used as a fallback for dumps that do not contain the module image
    // (Windows minidumps); the resolved field must still match the ClrMD field name
    // so an unrelated assembly at the same path is never trusted.
    private static bool TryResolveNullableUnderlyingFromModuleFile(
        ClrInstanceField field,
        ClrModule module,
        out ClrElementType element,
        out string typeName,
        out string? failure)
    {
        element = ClrElementType.Unknown;
        typeName = string.Empty;
        failure = null;

        try
        {
            var path = module.Name;
            if (string.IsNullOrWhiteSpace(path))
            {
                failure = "Nullable module file unavailable";
                return false;
            }

            var file = new FileInfo(path);
            if (!file.Exists)
            {
                failure = "Nullable module file unavailable";
                return false;
            }

            if (file.Length is <= 0 or > 512L * 1024 * 1024)
            {
                failure = "Nullable module file unavailable";
                return false;
            }

            var image = File.ReadAllBytes(path);
            return TryParseNullableFieldSignature(
                field,
                image,
                verifyFieldName: true,
                out element,
                out typeName,
                out failure);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                IOException or
                UnauthorizedAccessException or
                NotSupportedException)
        {
            failure = "Nullable module file read failed";
            return false;
        }
    }

    private static bool TryParseNullableFieldSignature(
        ClrInstanceField field,
        byte[] metadataImage,
        bool verifyFieldName,
        out ClrElementType element,
        out string typeName,
        out string? failure) =>
        TryParseNullableFieldSignature(
            metadataImage,
            field.Token & 0x00FFFFFF,
            verifyFieldName ? field.Name : null,
            out element,
            out typeName,
            out failure);

    // Parses the closed Nullable<T> generic argument out of a field signature in a
    // module metadata image. expectedFieldName is set when the image is not the one
    // captured with the dump (module file fallback) so a stale or unrelated assembly
    // at the same path is never trusted.
    internal static bool TryParseNullableFieldSignature(
        byte[] metadataImage,
        int fieldToken,
        string? expectedFieldName,
        out ClrElementType element,
        out string typeName,
        out string? failure)
    {
        element = ClrElementType.Unknown;
        typeName = string.Empty;
        failure = null;

        var metadataStart = FindMetadataRoot(metadataImage);
        if (metadataStart < 0)
        {
            failure = "Nullable metadata root not found";
            return false;
        }

        try
        {
            var handle = MetadataTokens.FieldDefinitionHandle(fieldToken);
            using var provider = MetadataReaderProvider.FromMetadataImage(
                ImmutableArray.Create(
                    metadataImage,
                    metadataStart,
                    metadataImage.Length - metadataStart));
            var metadata = provider.GetMetadataReader();

            var fieldDefinition = metadata.GetFieldDefinition(handle);
            if (expectedFieldName is not null &&
                !string.Equals(
                    metadata.GetString(fieldDefinition.Name),
                    expectedFieldName,
                    StringComparison.Ordinal))
            {
                failure = "Nullable metadata field mismatch";
                return false;
            }

            var signature = metadata.GetBlobReader(fieldDefinition.Signature);
            if (signature.ReadByte() != 0x06) // FIELD
            {
                failure = "Nullable field signature not found";
                return false;
            }

            var typeCode = signature.ReadByte();
            switch (typeCode)
            {
                // ELEMENT_TYPE_GENERICINST: the generic value type (Nullable`1) and its
                // arguments are inlined right after the field signature header.
                case 0x15:
                    if (signature.ReadByte() != 0x11) // VALUETYPE
                    {
                        failure = "Nullable signature kind mismatch";
                        return false;
                    }

                    _ = signature.ReadCompressedInteger(); // Nullable`1 TypeDefOrRef token
                    if (signature.ReadCompressedInteger() < 1) // argument count
                    {
                        failure = "Nullable signature has no arguments";
                        return false;
                    }

                    break;

                // ELEMENT_TYPE_VALUETYPE through a TypeSpec token (alternate encoding).
                case 0x11:
                {
                    var token = signature.ReadCompressedInteger();
                    if ((token & 0x3) != 2) // TypeSpec
                    {
                        failure = "Nullable signature is not a TypeSpec";
                        return false;
                    }

                    var typeSpec = metadata.GetTypeSpecification(
                        MetadataTokens.TypeSpecificationHandle(token >> 2));
                    var typeSpecBytes = metadata.GetBlobReader(typeSpec.Signature);
                    if (typeSpecBytes.ReadByte() != 0x15 || // GENERICINST
                        typeSpecBytes.ReadByte() != 0x11) // VALUETYPE
                    {
                        failure = "Nullable TypeSpec mismatch";
                        return false;
                    }

                    _ = typeSpecBytes.ReadCompressedInteger(); // Nullable`1 TypeDefOrRef token
                    if (typeSpecBytes.ReadCompressedInteger() < 1) // argument count
                    {
                        failure = "Nullable TypeSpec has no arguments";
                        return false;
                    }

                    signature = typeSpecBytes; // continue reading the argument list below
                    break;
                }

                default:
                    failure = $"Nullable signature code 0x{typeCode:X2} unsupported";
                    return false;
            }

            var argumentCode = signature.ReadByte();
            while (argumentCode is 0x1E or 0x1F) // optional/required modifiers
            {
                _ = signature.ReadCompressedInteger();
                argumentCode = signature.ReadByte();
            }

            var mapped = TryMapElementCode(argumentCode, out element, out typeName);
            if (!mapped)
            {
                failure = $"Nullable argument code 0x{argumentCode:X2} unsupported";
            }

            return mapped;
        }
        catch (Exception exception)
        {
            failure = $"Nullable metadata parse failed ({exception.GetType().Name})";
            return false;
        }
    }

    // Some data readers report module metadata roots that start directly at the
    // BSJB signature, while module files and whole-image dumps embed the metadata
    // inside a PE container. Locate the root through the PE CLI header when the
    // image is a file, and scan a little as a last resort for reader layouts that
    // expose neither.
    private static int FindMetadataRoot(byte[] image)
    {
        if (image.Length >= 4 &&
            image[0] == 'B' && image[1] == 'S' && image[2] == 'J' && image[3] == 'B')
        {
            return 0;
        }

        var peRoot = TryFindPeMetadataRoot(image);
        if (peRoot >= 0)
        {
            return peRoot;
        }

        var limit = Math.Min(image.Length - 4, 4096);
        for (var index = 0; index <= limit; index++)
        {
            if (image[index] == 'B' &&
                image[index + 1] == 'S' &&
                image[index + 2] == 'J' &&
                image[index + 3] == 'B')
            {
                return index;
            }
        }

        return -1;
    }

    // Walks the PE headers of an on-disk module image to the CLI metadata directory
    // and maps its RVA back to a file offset. Returns -1 when the image is not a PE
    // file or the metadata directory cannot be resolved.
    private static int TryFindPeMetadataRoot(byte[] image)
    {
        if (image.Length < 0x40 ||
            image[0] != (byte)'M' || image[1] != (byte)'Z')
        {
            return -1;
        }

        var peOffset = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
            image.AsSpan(0x3C, 4));
        if (peOffset <= 0 ||
            peOffset + 4 + 20 > image.Length ||
            image[peOffset] != (byte)'P' ||
            image[peOffset + 1] != (byte)'E' ||
            image[peOffset + 2] != 0 ||
            image[peOffset + 3] != 0)
        {
            return -1;
        }

        var coff = peOffset + 4;
        var sectionCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(
            image.AsSpan(coff + 2, 2));
        var optionalSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(
            image.AsSpan(coff + 16, 2));
        var optional = coff + 20;
        if (sectionCount == 0 || optionalSize < 2 || optional + optionalSize > image.Length)
        {
            return -1;
        }

        // Data directory offsets differ between the PE32 and PE32+ optional headers.
        var magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(
            image.AsSpan(optional, 2));
        var directoryOffset = magic switch
        {
            0x10B => optional + 96,  // PE32
            0x20B => optional + 112, // PE32+
            _ => -1,
        };
        if (directoryOffset < 0 || directoryOffset + 14 * 8 + 8 > image.Length)
        {
            return -1;
        }

        var directoryCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            image.AsSpan(directoryOffset - 4, 4));
        if (directoryCount <= 14)
        {
            return -1;
        }

        // Data directory 14 is the CLI header; its metadata RVA points at the BSJB root.
        var cliRva = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            image.AsSpan(directoryOffset + 14 * 8, 4));
        if (cliRva == 0)
        {
            return -1;
        }

        var sectionTable = optional + optionalSize;
        int MapRva(uint rva)
        {
            for (var index = 0; index < sectionCount; index++)
            {
                var entry = sectionTable + index * 40;
                if (entry + 40 > image.Length)
                {
                    return -1;
                }

                var virtualSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                    image.AsSpan(entry + 8, 4));
                var virtualAddress = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                    image.AsSpan(entry + 12, 4));
                var rawSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                    image.AsSpan(entry + 16, 4));
                var rawPointer = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                    image.AsSpan(entry + 20, 4));
                var mappedSize = Math.Min(virtualSize, rawSize);
                if (rawPointer != 0 &&
                    rva >= virtualAddress &&
                    rva - virtualAddress < mappedSize &&
                    rawPointer + (rva - virtualAddress) < image.Length)
                {
                    return (int)(rawPointer + (rva - virtualAddress));
                }
            }

            return -1;
        }

        var cliOffset = MapRva(cliRva);
        if (cliOffset < 0 || cliOffset + 16 > image.Length)
        {
            return -1;
        }

        // CLI header layout: cb(4) MajorRuntimeVersion(2) MinorRuntimeVersion(2)
        // MetaData RVA(4) MetaData Size(4).
        var metadataRva = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            image.AsSpan(cliOffset + 8, 4));
        var metadataRoot = MapRva(metadataRva);
        if (metadataRoot < 0 ||
            metadataRoot + 4 > image.Length ||
            image[metadataRoot] != (byte)'B' ||
            image[metadataRoot + 1] != (byte)'S' ||
            image[metadataRoot + 2] != (byte)'J' ||
            image[metadataRoot + 3] != (byte)'B')
        {
            return -1;
        }

        return metadataRoot;
    }

    private static bool TryReadByte(object reader, ulong address, out byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        value = 0;
        if (ReadExactly(reader, address, buffer) == 1)
        {
            value = buffer[0];
            return true;
        }

        return false;
    }

    private static int ReadExactly(object reader, ulong address, Span<byte> buffer) =>
        reader is IMemoryReader memory
            ? memory.Read(address, buffer)
            : 0;

    private static HeapFieldValue DecodeValueTypeMember(
        ClrInstanceField field,
        ClrValueType valueType,
        ClrInstanceField valueField,
        ClrObject obj,
        ObjectValueReadOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            if (valueField.Type?.BaseType?.Name == "System.Enum")
            {
                return ReadEnum(valueField, valueType);
            }

            return valueField.ElementType switch
            {
                ClrElementType.Boolean => Primitive(field, valueType.ReadField<bool>(valueField)),
                ClrElementType.Char => Character(field, valueType.ReadField<char>(valueField)),
                ClrElementType.Int8 => Primitive(field, valueType.ReadField<sbyte>(valueField)),
                ClrElementType.UInt8 => Primitive(field, valueType.ReadField<byte>(valueField)),
                ClrElementType.Int16 => Primitive(field, valueType.ReadField<short>(valueField)),
                ClrElementType.UInt16 => Primitive(field, valueType.ReadField<ushort>(valueField)),
                ClrElementType.Int32 => PrimitiveOrEnum(field, valueType.ReadField<int>(valueField)),
                ClrElementType.UInt32 => PrimitiveOrEnum(field, valueType.ReadField<uint>(valueField)),
                ClrElementType.Int64 => PrimitiveOrEnum(field, valueType.ReadField<long>(valueField)),
                ClrElementType.UInt64 => PrimitiveOrEnum(field, valueType.ReadField<ulong>(valueField)),
                ClrElementType.Float => Primitive(field, valueType.ReadField<float>(valueField)),
                ClrElementType.Double => Primitive(field, valueType.ReadField<double>(valueField)),
                ClrElementType.String => String(field, valueType.ReadObjectField(valueField), options.StringLimit),
                ClrElementType.Struct => DecodeNestedStruct(field, valueType, valueField, obj, options, cancellationToken),
                _ => Unavailable(field, "Unsupported value type"),
            };
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                InvalidOperationException or
                IOException or
                ClrDiagnosticsException)
        {
            return Unavailable(field, "Value could not be read");
        }
    }

    private static HeapFieldValue DecodeNestedStruct(
        ClrInstanceField field,
        ClrValueType parent,
        ClrInstanceField valueField,
        ClrObject obj,
        ObjectValueReadOptions options,
        CancellationToken cancellationToken)
    {
        var nested = parent.ReadValueTypeField(valueField);
        if (!nested.IsValid)
        {
            return Unavailable(field, "Unsupported value type");
        }

        return DecodeValueType(field, nested, obj, options, cancellationToken);
    }

    private static HeapFieldValue ReadEnum(ClrInstanceField field, ClrValueType valueType)
    {
        var enumType = valueType.Type!;
        var elementType = enumType.AsEnum().ElementType;
        var value = ReadValueTypePrimitive(valueType, field, elementType);
        var numeric = Invariant(value);
        var name = EnumName(enumType, value);
        var text = name is null ? numeric : $"{name} ({numeric})";
        return Enum(field, text);
    }

    private static object ReadValueTypePrimitive(ClrValueType valueType, ClrInstanceField field, ClrElementType elementType) =>
        elementType switch
        {
            ClrElementType.Boolean => valueType.ReadField<bool>(field),
            ClrElementType.Char => valueType.ReadField<char>(field),
            ClrElementType.Int8 => valueType.ReadField<sbyte>(field),
            ClrElementType.UInt8 => valueType.ReadField<byte>(field),
            ClrElementType.Int16 => valueType.ReadField<short>(field),
            ClrElementType.UInt16 => valueType.ReadField<ushort>(field),
            ClrElementType.Int32 => valueType.ReadField<int>(field),
            ClrElementType.UInt32 => valueType.ReadField<uint>(field),
            ClrElementType.Int64 => valueType.ReadField<long>(field),
            ClrElementType.UInt64 => valueType.ReadField<ulong>(field),
            ClrElementType.Float => valueType.ReadField<float>(field),
            ClrElementType.Double => valueType.ReadField<double>(field),
            _ => throw new InvalidOperationException("Unsupported enum underlying type."),
        };

    private static decimal ReadDecimal(ClrValueType vt)
    {
        var lo64 = vt.ReadField<ulong>("_lo64");
        var hi32 = vt.ReadField<uint>("_hi32");
        var flags = vt.ReadField<int>("_flags");
        var lo = unchecked((int)(uint)(lo64 & 0xFFFFFFFF));
        var mid = unchecked((int)(uint)(lo64 >> 32));
        var hi = unchecked((int)hi32);
        var negative = (flags & unchecked((int)0x80000000)) != 0;
        var scale = (byte)((flags >> 16) & 0x7F);
        return new decimal(lo, mid, hi, negative, scale);
    }

    private static DateTime ReadDateTime(ClrValueType vt)
    {
        var dateData = vt.ReadField<ulong>("_dateData");
        var ticks = (long)(dateData & 0x3FFFFFFFFFFFFFFFUL);
        var kind = (int)(dateData >> 62);
        var dateTimeKind = kind switch
        {
            1 => DateTimeKind.Utc,
            2 => DateTimeKind.Local,
            _ => DateTimeKind.Unspecified,
        };
        return new DateTime(ticks, dateTimeKind);
    }

    private static TimeSpan ReadTimeSpan(ClrValueType vt) =>
        TimeSpan.FromTicks(vt.ReadField<long>("_ticks"));

    private static Guid ReadGuid(ClrValueType vt) =>
        new(
            vt.ReadField<int>("_a"),
            vt.ReadField<short>("_b"),
            vt.ReadField<short>("_c"),
            vt.ReadField<byte>("_d"),
            vt.ReadField<byte>("_e"),
            vt.ReadField<byte>("_f"),
            vt.ReadField<byte>("_g"),
            vt.ReadField<byte>("_h"),
            vt.ReadField<byte>("_i"),
            vt.ReadField<byte>("_j"),
            vt.ReadField<byte>("_k"));

    private static HeapFieldValue String(ClrInstanceField field, ClrObject parent, int stringLimit)
    {
        var reference = parent.ReadObjectField(field);
        if (reference.IsNull)
        {
            return Null(field);
        }

        var value = reference.AsString(stringLimit) ?? string.Empty;
        var totalLength = TryReadStringLength(reference);
        var truncated = totalLength is int length && length > value.Length;
        return Field(
            field.Name,
            $"{field.Type?.Name ?? string.Empty}",
            HeapValueKind.String,
            value,
            reference.Address,
            reference.Type?.Name,
            truncated,
            totalLength,
            null);
    }

    private static int? TryReadStringLength(ClrObject reference)
    {
        try
        {
            return reference.ReadField<int>("_stringLength");
        }
        catch
        {
            // Fall through to the alternate runtime field name.
        }

        try
        {
            return reference.ReadField<int>("m_stringLength");
        }
        catch
        {
            return null;
        }
    }

    private static HeapFieldValue ReferenceField(ClrObject parent, ClrInstanceField field)
    {
        var raw = parent.ReadField<ulong>(field);
        var target = parent.Type!.Heap.GetObject(raw);
        return Reference(field.Name, $"{field.Type?.Name ?? string.Empty}", target);
    }

    private static HeapFieldValue Reference(string? name, string? typeName, ClrObject target) =>
        target.IsNull || !target.IsValid || target.IsFree || target.Address == 0
            ? Field(name, typeName, HeapValueKind.Null, null, null, null, false, null, null)
            : Field(name, typeName, HeapValueKind.ObjectReference, null, target.Address, target.Type?.Name, false, null, null);

    private static string? EnumName(ClrType enumType, object value)
    {
        try
        {
            foreach (var (name, enumValue) in enumType.AsEnum().EnumerateValues())
            {
                if (EnumValuesEqual(enumValue, value))
                {
                    return name;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool EnumValuesEqual(object? left, object? right)
    {
        if (object.Equals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        try
        {
            return Convert.ToDecimal(left, CultureInfo.InvariantCulture) ==
                   Convert.ToDecimal(right, CultureInfo.InvariantCulture);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasField(ClrType type, string name) =>
        type.Fields.Any(field => field.Name == name);

    private static string Invariant(object value) =>
        value switch
        {
            double number => HeapValueFormatting.Scalar(number),
            float number => HeapValueFormatting.Scalar(number),
            DateTime date => HeapValueFormatting.Scalar(date),
            TimeSpan duration => HeapValueFormatting.Scalar(duration),
            Guid guid => HeapValueFormatting.Scalar(guid),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

    private static HeapFieldValue Primitive(ClrInstanceField field, object value) =>
        Field(field, HeapValueKind.Primitive, Invariant(value), null, null, null);

    private static HeapFieldValue Primitive(string name, object value) =>
        Field(name, string.Empty, HeapValueKind.Primitive, Invariant(value), null, null, false, null, null);

    private static HeapFieldValue ArrayElementScalar(string name, string declaredTypeName, object value) =>
        Field(name, declaredTypeName, HeapValueKind.ArrayElement, Invariant(value), null, null, false, null, null);

    private static HeapFieldValue ArrayElementReference(string name, string declaredTypeName, ClrObject target) =>
        Field(
            name,
            declaredTypeName,
            HeapValueKind.ArrayElement,
            null,
            target.IsNull || !target.IsValid || target.IsFree || target.Address == 0 ? null : target.Address,
            target.IsNull || !target.IsValid || target.IsFree || target.Address == 0 ? null : target.Type?.Name,
            false,
            null,
            null);

    private static HeapFieldValue ArrayElementUnavailable(string name, string declaredTypeName, string reason) =>
        Field(name, declaredTypeName, HeapValueKind.ArrayElement, null, null, null, false, null, reason);

    private static HeapFieldValue Character(ClrInstanceField field, char value) =>
        Field(field, HeapValueKind.Primitive, HeapValueFormatting.Character(value), null, null, null);

    private static HeapFieldValue Character(string name, char value) =>
        Field(name, string.Empty, HeapValueKind.Primitive, HeapValueFormatting.Character(value), null, null, false, null, null);

    private static HeapFieldValue Enum(ClrInstanceField field, string text) =>
        Field(field, HeapValueKind.Enum, text, null, null, null);

    private static HeapFieldValue Null(ClrInstanceField field) =>
        Field(field, HeapValueKind.Null, null, null, null, null);

    private static HeapFieldValue Null(ClrInstanceField field, string declaredTypeName) =>
        Field(
            field.Name ?? string.Empty,
            declaredTypeName,
            HeapValueKind.Null,
            null,
            null,
            null,
            false,
            null,
            null);

    private static HeapFieldValue Unavailable(ClrInstanceField field, string reason) =>
        Field(field, HeapValueKind.Unavailable, null, null, null, reason);

    private static HeapFieldValue Unavailable(string name, string reason) =>
        Field(name, string.Empty, HeapValueKind.Unavailable, null, null, null, false, null, reason);

    private static HeapFieldValue Field(
        ClrInstanceField field,
        HeapValueKind kind,
        string? value,
        ulong? referenceAddress,
        string? referenceType,
        string? unavailableReason) =>
        Field(field.Name ?? string.Empty, $"{field.Type?.Name ?? string.Empty}", kind, value,
            referenceAddress, referenceType, false, null, unavailableReason);

    private static HeapFieldValue Field(
        string? name,
        string? typeName,
        HeapValueKind kind,
        string? value,
        ulong? referenceAddress,
        string? referenceType,
        bool isTruncated,
        int? totalLength,
        string? unavailableReason) =>
        new(
            name ?? string.Empty,
            typeName ?? string.Empty,
            kind,
            value,
            referenceAddress,
            referenceType,
            isTruncated,
            totalLength,
            unavailableReason);
}
