using System.Globalization;
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
                ReadArrayElement(array, start + index, elementType, isReference, cancellationToken));
        }

        var hasMore = start + count < length;
        return new HeapObjectValueResult(header, fields, length, hasMore);
    }

    private static HeapFieldValue ReadArrayElement(
        ClrArray array,
        int index,
        ClrElementType elementType,
        bool isReference,
        CancellationToken cancellationToken)
    {
        var name = $"[{index}]";
        if (isReference)
        {
            return Reference(name, "System.Array", array.GetObjectValue(index));
        }

        return elementType switch
        {
            ClrElementType.Boolean => Primitive(name, array.GetValue<bool>(index)),
            ClrElementType.Char => Character(name, array.GetValue<char>(index)),
            ClrElementType.Int8 => Primitive(name, array.GetValue<sbyte>(index)),
            ClrElementType.UInt8 => Primitive(name, array.GetValue<byte>(index)),
            ClrElementType.Int16 => Primitive(name, array.GetValue<short>(index)),
            ClrElementType.UInt16 => Primitive(name, array.GetValue<ushort>(index)),
            ClrElementType.Int32 => Primitive(name, array.GetValue<int>(index)),
            ClrElementType.UInt32 => Primitive(name, array.GetValue<uint>(index)),
            ClrElementType.Int64 => Primitive(name, array.GetValue<long>(index)),
            ClrElementType.UInt64 => Primitive(name, array.GetValue<ulong>(index)),
            ClrElementType.Float => Primitive(name, array.GetValue<float>(index)),
            ClrElementType.Double => Primitive(name, array.GetValue<double>(index)),
            _ => Unavailable(name, "Unsupported value type"),
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

            return DecodeValueType(field, vt, options, cancellationToken);
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
            return DecodeNullable(field, vt, options, cancellationToken);
        }

        return Unavailable(field, "Unsupported value type");
    }

    private static HeapFieldValue DecodeNullable(
        ClrInstanceField field,
        ClrValueType nullable,
        ObjectValueReadOptions options,
        CancellationToken cancellationToken)
    {
        var hasValue = nullable.ReadField<bool>("hasValue");
        if (!hasValue)
        {
            return Null(field);
        }

        var valueField = nullable.Type!.Fields.FirstOrDefault(candidate => candidate.Name == "value");
        if (valueField is null)
        {
            return Unavailable(field, "Unsupported value type");
        }

        return DecodeValueTypeMember(field, nullable, valueField, options, cancellationToken);
    }

    private static HeapFieldValue DecodeValueTypeMember(
        ClrInstanceField field,
        ClrValueType valueType,
        ClrInstanceField valueField,
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
                ClrElementType.Struct => DecodeNestedStruct(field, valueType, valueField, options, cancellationToken),
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
        ObjectValueReadOptions options,
        CancellationToken cancellationToken)
    {
        var nested = parent.ReadValueTypeField(valueField);
        if (!nested.IsValid)
        {
            return Unavailable(field, "Unsupported value type");
        }

        return DecodeValueType(field, nested, options, cancellationToken);
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

    private static HeapFieldValue Character(ClrInstanceField field, char value) =>
        Field(field, HeapValueKind.Primitive, HeapValueFormatting.Character(value), null, null, null);

    private static HeapFieldValue Character(string name, char value) =>
        Field(name, string.Empty, HeapValueKind.Primitive, HeapValueFormatting.Character(value), null, null, false, null, null);

    private static HeapFieldValue Enum(ClrInstanceField field, string text) =>
        Field(field, HeapValueKind.Enum, text, null, null, null);

    private static HeapFieldValue Null(ClrInstanceField field) =>
        Field(field, HeapValueKind.Null, null, null, null, null);

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
