using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace GridSim.Core.Dynamics.Plugins;

/// <summary>What the verifier found in a plug-in assembly's metadata, before anything was loaded.</summary>
/// <param name="AssemblyName">The assembly's simple name.</param>
/// <param name="ContractVersion">The <see cref="GridSimModelPluginAttribute"/> version, or null when the attribute is absent.</param>
/// <param name="Refusals">Every refused reference, in a fixed order (method bodies in metadata order, then the remaining
/// metadata tables), each naming the referenced member and the type (and method) that references it.</param>
public sealed record PluginVerification(string AssemblyName, int? ContractVersion, IReadOnlyList<string> Refusals);

/// <summary>
/// The determinism lint of a model plug-in (contract version 1), run on the assembly's bytes with
/// <see cref="System.Reflection.Metadata"/> BEFORE the assembly is loaded: it reads the plug-in attribute, the assembly
/// references, every method body's instruction stream (each member, field, type and signature token, and the single-precision
/// opcodes), the signatures of every method, field and local, every P/Invoke import and body-less (extern) method, and finally
/// every type and member reference in the metadata tables. A reference on <see cref="PluginDenylist"/> is refused, naming it
/// and the referencing type. A static field of another assembly may not be written (<c>stsfld</c>) or have its address taken
/// (<c>ldsflda</c>), and may be read (<c>ldsfld</c>) only when it holds a primitive number, a bool, a char or a string, so no
/// shared array or object of the host is reachable to write through. A GridSim.Core type outside
/// <see cref="PluginDenylist.IsAllowedCoreType"/> is refused, by name and by the assembly its reference resolves to. It is a
/// lint against accidents, not a sandbox: .NET has no in-process security boundary.
/// </summary>
public static class PluginVerifier
{
    private const string AttributeNamespace = "GridSim.Core.Dynamics.Plugins";
    private const string AttributeName = "GridSimModelPluginAttribute";

    private static readonly Dictionary<short, OperandType> OperandOf = BuildOperandTable();

    private static Dictionary<short, OperandType> BuildOperandTable()
    {
        // The opcode table of the runtime itself (System.Reflection.Emit.OpCodes), read once; used for operand sizes only.
        var d = new Dictionary<short, OperandType>();
        foreach (FieldInfo f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (f.GetValue(null) is OpCode op) d[op.Value] = op.OperandType;
        return d;
    }

    /// <summary>Verify the assembly bytes. Throws <see cref="BadImageFormatException"/> for bytes that are not a .NET assembly.</summary>
    public static PluginVerification Verify(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var pe = new PEReader(ImmutableArray.Create(bytes));
        if (!pe.HasMetadata) throw new BadImageFormatException("the file is not a .NET assembly (no metadata).");
        MetadataReader md = pe.GetMetadataReader();
        if (!md.IsAssembly) throw new BadImageFormatException("the file is a module, not an assembly.");
        AssemblyDefinition asm = md.GetAssemblyDefinition();
        string name = md.GetString(asm.Name);
        int? version = ReadContractVersion(md, asm);
        // Not a plug-in: nothing further is read (the loader refuses it for the missing attribute).
        if (version is null) return new PluginVerification(name, null, Array.Empty<string>());

        var refusals = new List<string>();
        var reported = new HashSet<string>(StringComparer.Ordinal);   // lookup only; the list keeps the order
        void Refuse(string target, string by, string why)
        {
            if (reported.Add(target)) refusals.Add($"{target} (referenced by {by}): {why}");
        }

        foreach (AssemblyReferenceHandle h in md.AssemblyReferences)
        {
            string an = md.GetString(md.GetAssemblyReference(h).Name);
            if (!PluginDenylist.AllowedAssemblyReference(an))
                Refuse("assembly " + an, name, "a plug-in references only the framework and GridSim.Core");
        }
        for (int r = 1; r <= md.GetTableRowCount(TableIndex.ModuleRef); r++)
            Refuse("native module " + md.GetString(md.GetModuleReference(MetadataTokens.ModuleReferenceHandle(r)).Name), name, "a plug-in calls no native code (P/Invoke)");

        var sig = new NameProvider();
        foreach (TypeDefinitionHandle th in md.TypeDefinitions)
        {
            TypeDefinition td = md.GetTypeDefinition(th);
            string typeName = sig.GetTypeFromDefinition(md, th, 0);
            // The runtime honours an IgnoresAccessChecksTo attribute that the plug-in defines itself (it lifts the access
            // checks on another assembly's private members), so defining it is refused like referencing it.
            if (typeName == IgnoresAccessChecks)
                Refuse("the type " + typeName + " defined in " + name, name, "a plug-in may not lift the runtime's access checks");
            foreach (FieldDefinitionHandle fh in td.GetFields())
            {
                FieldDefinition fd = md.GetFieldDefinition(fh);
                string where = typeName + "." + md.GetString(fd.Name);
                CheckNames(sig, () => fd.DecodeSignature(sig, null), where, Refuse);
            }
            foreach (MethodDefinitionHandle mh in td.GetMethods())
            {
                MethodDefinition def = md.GetMethodDefinition(mh);
                string where = typeName + "." + md.GetString(def.Name);
                if ((def.Attributes & MethodAttributes.PinvokeImpl) != 0)
                    Refuse(where + " (P/Invoke)", typeName, "a plug-in calls no native code (P/Invoke)");
                CheckNames(sig, () => def.DecodeSignature(sig, null), where, Refuse);
                if (def.RelativeVirtualAddress == 0)
                {
                    // No body: abstract and runtime-implemented (delegate) methods are fine; any other is extern, whose
                    // implementation the runtime supplies (UnsafeAccessor, InternalCall), so it is not the plug-in's IL.
                    if ((def.Attributes & MethodAttributes.Abstract) == 0 && (def.ImplAttributes & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.Runtime
                        && (def.Attributes & MethodAttributes.PinvokeImpl) == 0)
                        Refuse(where + " (extern)", typeName, "a plug-in method has a body of its own (no extern or runtime-supplied method)");
                    continue;
                }
                MethodBodyBlock body = pe.GetMethodBody(def.RelativeVirtualAddress);
                if (!body.LocalSignature.IsNil)
                {
                    StandaloneSignature ls = md.GetStandaloneSignature(body.LocalSignature);
                    CheckNames(sig, () => ls.DecodeLocalSignature(sig, null), where, Refuse);
                }
                ScanIl(md, sig, body.GetILReader(), where, Refuse);
            }
        }
        // Every reference left in the tables (attribute constructors, base types and the like) that no body named.
        foreach (TypeReferenceHandle h in md.TypeReferences)
        {
            string t = sig.GetTypeFromReference(md, h, 0);
            if (PluginDenylist.Refusal(t, null) is { } why) Refuse(t, name + " (metadata)", why);
            // By the assembly it resolves to, so a GridSim.Core type is held to the namespaces whatever its name.
            else if (ResolvesToCore(md, h) && !PluginDenylist.IsAllowedCoreType(t))
                Refuse(t, name + " (metadata)", $"{t} is outside the plug-in contract (a plug-in uses GridSim.Core only in GridSim.Core.Dynamics.Models, GridSim.Core.Dynamics.Plugins and GridSim.Core.Numerics)");
        }
        foreach (MemberReferenceHandle h in md.MemberReferences)
        {
            (string t, string m) = MemberName(md, sig, h);
            if (PluginDenylist.Refusal(t, m) is { } why) Refuse(t + "." + m, name + " (metadata)", why);
        }
        return new PluginVerification(name, version, refusals);
    }

    private const string IgnoresAccessChecks = "System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute";

    /// <summary>True when the type reference (followed out through its declaring types) resolves to the GridSim.Core assembly.</summary>
    private static bool ResolvesToCore(MetadataReader md, TypeReferenceHandle h)
    {
        EntityHandle scope = md.GetTypeReference(h).ResolutionScope;
        while (scope.Kind == HandleKind.TypeReference) scope = md.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
        return scope.Kind == HandleKind.AssemblyReference
            && md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name) == "GridSim.Core";
    }

    private static int? ReadContractVersion(MetadataReader md, AssemblyDefinition asm)
    {
        foreach (CustomAttributeHandle h in asm.GetCustomAttributes())
        {
            CustomAttribute ca = md.GetCustomAttribute(h);
            if (ca.Constructor.Kind != HandleKind.MemberReference) continue;   // the attribute must come from GridSim.Core
            MemberReference ctor = md.GetMemberReference((MemberReferenceHandle)ca.Constructor);
            if (ctor.Parent.Kind != HandleKind.TypeReference) continue;
            TypeReference tr = md.GetTypeReference((TypeReferenceHandle)ctor.Parent);
            if (md.GetString(tr.Namespace) != AttributeNamespace || md.GetString(tr.Name) != AttributeName) continue;
            if (tr.ResolutionScope.Kind != HandleKind.AssemblyReference
                || md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope).Name) != "GridSim.Core") continue;
            BlobReader br = md.GetBlobReader(ca.Value);
            if (br.Length < 6 || br.ReadUInt16() != 1) continue;
            return br.ReadInt32();
        }
        return null;
    }

    private static void CheckNames(NameProvider sig, Action decode, string where, Action<string, string, string> refuse)
    {
        sig.Seen.Clear();
        try { decode(); }
        catch (BadImageFormatException) { refuse(where + " (signature)", where, "the signature cannot be read"); return; }
        foreach (string t in sig.Seen)
        {
            if (t is "System.Single") refuse("System.Single (float) in " + where, where, "a plug-in computes in double precision only");
            else if (t.StartsWith("pointer:", StringComparison.Ordinal)) refuse("a pointer type in " + where, where, "a plug-in uses no unsafe code");
            else if (PluginDenylist.Refusal(t, null) is { } why) refuse(t + " in " + where, where, why);
        }
    }

    private static (string Type, string Member) MemberName(MetadataReader md, NameProvider sig, MemberReferenceHandle h)
    {
        MemberReference mr = md.GetMemberReference(h);
        string member = md.GetString(mr.Name);
        string type = mr.Parent.Kind switch
        {
            HandleKind.TypeReference => sig.GetTypeFromReference(md, (TypeReferenceHandle)mr.Parent, 0),
            HandleKind.TypeDefinition => sig.GetTypeFromDefinition(md, (TypeDefinitionHandle)mr.Parent, 0),
            HandleKind.TypeSpecification => Head(md.GetTypeSpecification((TypeSpecificationHandle)mr.Parent).DecodeSignature(sig, null)),
            HandleKind.MethodDefinition => "<module>",
            HandleKind.ModuleReference => "native module " + md.GetString(md.GetModuleReference((ModuleReferenceHandle)mr.Parent).Name),
            _ => "<unknown>",
        };
        return (type, member);
    }

    private static string Head(string t)
    {
        int k = t.IndexOf('<', StringComparison.Ordinal);
        return k < 0 ? t : t.Substring(0, k);
    }

    private static void ScanIl(MetadataReader md, NameProvider sig, BlobReader il, string where, Action<string, string, string> refuse)
    {
        while (il.RemainingBytes > 0)
        {
            short code = il.ReadByte();
            if (code == 0xFE) code = unchecked((short)(0xFE00 | il.ReadByte()));
            if (code == 0x22 || code == 0x6B)   // ldc.r4, conv.r4
                refuse("single-precision arithmetic in " + where, where, "a plug-in computes in double precision only");
            if (!OperandOf.TryGetValue(code, out OperandType ot)) throw new BadImageFormatException($"unknown IL opcode 0x{code:X4} in {where}.");
            switch (ot)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget: case OperandType.ShortInlineI: case OperandType.ShortInlineVar: il.ReadByte(); break;
                case OperandType.InlineVar: il.ReadInt16(); break;
                case OperandType.InlineI: case OperandType.InlineBrTarget: case OperandType.ShortInlineR: case OperandType.InlineString: il.ReadInt32(); break;
                case OperandType.InlineI8: case OperandType.InlineR: il.ReadInt64(); break;
                case OperandType.InlineSwitch: { int n = il.ReadInt32(); for (int k = 0; k < n; k++) il.ReadInt32(); break; }
                case OperandType.InlineField:
                {
                    int token = il.ReadInt32();
                    CheckStaticField(md, sig, code, token, where, refuse);
                    CheckToken(md, sig, token, where, refuse);
                    break;
                }
                case OperandType.InlineMethod:
                case OperandType.InlineType:
                case OperandType.InlineTok:
                case OperandType.InlineSig:
                    CheckToken(md, sig, il.ReadInt32(), where, refuse);
                    break;
                default: throw new BadImageFormatException($"unhandled IL operand type {ot} in {where}.");
            }
        }
    }

    private const short Ldsfld = 0x7E, Ldsflda = 0x7F, Stsfld = 0x80;

    /// <summary>The static field rule: a field of another assembly (a member reference whose parent is not one of the plug-in's
    /// own types) may not be written or have its address taken, and may be read only when its type is a primitive number,
    /// bool, char or string (immutable values: nothing of the host's can be written through them).</summary>
    private static void CheckStaticField(MetadataReader md, NameProvider sig, short code, int token, string where, Action<string, string, string> refuse)
    {
        if (code is not (Ldsfld or Ldsflda or Stsfld)) return;
        EntityHandle h = MetadataTokens.EntityHandle(token);
        if (h.Kind != HandleKind.MemberReference) return;   // a field definition: the plug-in's own
        var mrh = (MemberReferenceHandle)h;
        MemberReference mr = md.GetMemberReference(mrh);
        if (IsOwnType(md, mr.Parent)) return;
        (string t, string f) = MemberName(md, sig, mrh);
        string target = $"the static field {t}.{f}";
        if (code == Stsfld) { refuse(target + " (written)", where, "a plug-in writes static fields of its own types only"); return; }
        if (code == Ldsflda) { refuse(target + " (address taken)", where, "a plug-in takes the address of static fields of its own types only"); return; }
        string type;
        try { type = mr.DecodeFieldSignature(sig, null); }
        catch (BadImageFormatException) { refuse(target + " (signature)", where, "the signature cannot be read"); return; }
        if (type is not ("System.Boolean" or "System.Byte" or "System.Char" or "System.Double" or "System.Int16" or "System.Int32" or "System.Int64"
            or "System.SByte" or "System.UInt16" or "System.UInt32" or "System.UInt64" or "System.String"))
            refuse(target + " (read)", where, "a plug-in reads another assembly's static fields only when they hold a number, bool, char or string (an array or object there could be written through)");
    }

    /// <summary>True when a member reference's parent is one of the plug-in's own types (a definition, or a generic
    /// instantiation of one).</summary>
    private static bool IsOwnType(MetadataReader md, EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeDefinition => true,
        HandleKind.TypeSpecification => md.GetTypeSpecification((TypeSpecificationHandle)parent).DecodeSignature(OwnHead.Instance, null),
        _ => false,
    };

    /// <summary>Decodes a type specification to "is its head type defined in this assembly".</summary>
    private sealed class OwnHead : ISignatureTypeProvider<bool, object?>
    {
        public static readonly OwnHead Instance = new();
        public bool GetPrimitiveType(PrimitiveTypeCode typeCode) => false;
        public bool GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => true;
        public bool GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => false;
        public bool GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        public bool GetSZArrayType(bool elementType) => false;
        public bool GetArrayType(bool elementType, ArrayShape shape) => false;
        public bool GetByReferenceType(bool elementType) => false;
        public bool GetPointerType(bool elementType) => false;
        public bool GetPinnedType(bool elementType) => false;
        public bool GetFunctionPointerType(MethodSignature<bool> signature) => false;
        public bool GetGenericInstantiation(bool genericType, ImmutableArray<bool> typeArguments) => genericType;
        public bool GetGenericMethodParameter(object? genericContext, int index) => false;
        public bool GetGenericTypeParameter(object? genericContext, int index) => false;
        public bool GetModifiedType(bool modifier, bool unmodifiedType, bool isRequired) => unmodifiedType;
    }

    private static void CheckToken(MetadataReader md, NameProvider sig, int token, string where, Action<string, string, string> refuse)
    {
        EntityHandle h = MetadataTokens.EntityHandle(token);
        switch (h.Kind)
        {
            case HandleKind.MemberReference:
            {
                (string t, string m) = MemberName(md, sig, (MemberReferenceHandle)h);
                if (PluginDenylist.Refusal(t, m) is { } why) refuse(t + "." + m, where, why);
                MemberReference mr = md.GetMemberReference((MemberReferenceHandle)h);
                CheckNames(sig, () =>
                {
                    if (mr.GetKind() == MemberReferenceKind.Method) mr.DecodeMethodSignature(sig, null);
                    else mr.DecodeFieldSignature(sig, null);
                }, where, refuse);
                break;
            }
            case HandleKind.MethodSpecification:
            {
                MethodSpecification ms = md.GetMethodSpecification((MethodSpecificationHandle)h);
                CheckToken(md, sig, MetadataTokens.GetToken(ms.Method), where, refuse);
                CheckNames(sig, () => ms.DecodeSignature(sig, null), where, refuse);
                break;
            }
            case HandleKind.TypeReference:
            {
                string t = sig.GetTypeFromReference(md, (TypeReferenceHandle)h, 0);
                if (t is "System.Single") refuse("System.Single (float) in " + where, where, "a plug-in computes in double precision only");
                else if (PluginDenylist.Refusal(t, null) is { } why) refuse(t, where, why);
                break;
            }
            case HandleKind.TypeSpecification:
            {
                TypeSpecification ts = md.GetTypeSpecification((TypeSpecificationHandle)h);
                CheckNames(sig, () => ts.DecodeSignature(sig, null), where, refuse);
                break;
            }
            case HandleKind.StandaloneSignature:
                refuse("indirect call (calli) in " + where, where, "a plug-in makes no indirect calls");
                break;
            default: break;   // a method, field or type of the plug-in itself
        }
    }

    /// <summary>Names every type a signature mentions (and keeps them in <see cref="Seen"/>, in decode order).</summary>
    private sealed class NameProvider : ISignatureTypeProvider<string, object?>
    {
        public readonly List<string> Seen = new();

        private string Note(string s) { Seen.Add(s); return s; }

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => Note(typeCode switch
        {
            PrimitiveTypeCode.Boolean => "System.Boolean", PrimitiveTypeCode.Byte => "System.Byte", PrimitiveTypeCode.Char => "System.Char",
            PrimitiveTypeCode.Double => "System.Double", PrimitiveTypeCode.Int16 => "System.Int16", PrimitiveTypeCode.Int32 => "System.Int32",
            PrimitiveTypeCode.Int64 => "System.Int64", PrimitiveTypeCode.IntPtr => "System.IntPtr", PrimitiveTypeCode.Object => "System.Object",
            PrimitiveTypeCode.SByte => "System.SByte", PrimitiveTypeCode.Single => "System.Single", PrimitiveTypeCode.String => "System.String",
            PrimitiveTypeCode.TypedReference => "System.TypedReference", PrimitiveTypeCode.UInt16 => "System.UInt16",
            PrimitiveTypeCode.UInt32 => "System.UInt32", PrimitiveTypeCode.UInt64 => "System.UInt64", PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
            PrimitiveTypeCode.Void => "System.Void",
            _ => "System.<primitive>",
        });

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            TypeDefinition td = reader.GetTypeDefinition(handle);
            string n = reader.GetString(td.Name);
            if (td.GetDeclaringType() is { IsNil: false } outer) return GetTypeFromDefinition(reader, outer, rawTypeKind) + "+" + n;
            string ns = reader.GetString(td.Namespace);
            return ns.Length == 0 ? n : ns + "." + n;
        }

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            TypeReference tr = reader.GetTypeReference(handle);
            string n = reader.GetString(tr.Name);
            string full;
            if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
                full = GetTypeFromReference(reader, (TypeReferenceHandle)tr.ResolutionScope, rawTypeKind) + "+" + n;
            else
            {
                string ns = reader.GetString(tr.Namespace);
                full = ns.Length == 0 ? n : ns + "." + n;
            }
            return Note(full);
        }

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[,]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetPointerType(string elementType) => Note("pointer:" + elementType);
        public string GetPinnedType(string elementType) => elementType;
        public string GetFunctionPointerType(MethodSignature<string> signature) => Note("pointer:function");
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";
        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    }
}
