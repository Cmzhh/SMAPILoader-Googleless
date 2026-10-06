using System.Numerics;
using System.Security.Cryptography;

using ELFSharp.ELF;
using ELFSharp.ELF.Sections;

namespace LibPatcher;

abstract class LibPatcherBase<T> where T : struct, IUnsignedNumber<T> {
    public const string DotNetVersion = "9.0.17";

    public Dictionary<string, SymbolEntry<T>> MonoMethodMap { get; } = new();
    public string LibFile { get; }

    internal LibPatcherBase(string location, bool isLibraryFile = false) {
        LibFile = Path.GetFullPath(isLibraryFile ? location : Path.Combine(location, @$"packs\Microsoft.NETCore.App.Runtime.Mono.android-{ArchName}\{DotNetVersion}\runtimes\android-{ArchName}\native\libmonosgen-2.0.so"));
    }

    public void Patch() {
        var bakFile = LibFile + ".bak";
        var originalHash = GetFileHash(LibFile);
        Console.WriteLine($"Runtime: {DotNetVersion} ({ArchName})\nLibrary: {LibFile}\nCurrent SHA-256: {originalHash}");

        if (File.Exists(bakFile)) {
            RequireHash(bakFile, OriginalSha256, "backup");
            if (PatchedSha256 == null)
                throw new InvalidDataException("Cannot verify an existing runtime backup for this architecture.");
            RequireHash(LibFile, PatchedSha256, "patched runtime");
            Console.WriteLine($"Verified already patched runtime. Patched SHA-256: {originalHash}");
            return;
        }

        if (PatchedSha256 != null && originalHash == PatchedSha256) {
            Console.WriteLine($"Verified already patched runtime. Patched SHA-256: {originalHash}");
            return;
        }

        RequireHash(LibFile, OriginalSha256, "original runtime");
        ValidateLibraryHeader();

        PatchData[] patches = [
            Patch_FieldAccessException(),
            Patch_MethodAccessException(),
        ];
        var patchLocations = new List<(PatchData Patch, long Offset)>();

        using (var libReader = ELFReader.Load(LibFile)) {
            var sect = (SymbolTable<T>)libReader.GetSection(".dynsym");
            foreach (var item in sect.Entries) {
                if (item.Type == SymbolType.Function && item.Name.StartsWith("mono_")) {
                    MonoMethodMap[item.Name] = item;
                }
            }

            foreach (var patchData in patches) {
                var patchFileOffset = checked(long.CreateChecked(GetFunctionOffsetVAFile(patchData.ExportFunctionName)) + patchData.Offset);
                var section = GetFunction(patchData.ExportFunctionName).PointedSection;
                var sectionStart = long.CreateChecked(section.Offset);
                var sectionEnd = checked(sectionStart + long.CreateChecked(section.Size));
                if ((ulong.CreateChecked(section.RawFlags) & 0x4) == 0 || patchFileOffset < sectionStart || checked(patchFileOffset + patchData.PatchBytes.Length) > sectionEnd)
                    throw new InvalidDataException($"Patch {patchData.ExportFunctionName} is outside its executable ELF section.");
                ValidatePatchBytes(patchData, patchFileOffset);
                patchLocations.Add((patchData, patchFileOffset));
            }
        }

        File.Copy(LibFile, bakFile);

        try {
            using (var libWriter = File.Open(LibFile, FileMode.Open, FileAccess.ReadWrite)) {
                foreach (var (patchData, patchFileOffset) in patchLocations) {
                    Console.WriteLine($$"""
                    Patch: {{patchData.ExportFunctionName}}
                        Symbol offset    : 0x{{patchData.Offset:X}}
                        Byte length      : {{patchData.PatchBytes.Length}}
                        Patch file offset: 0x{{patchFileOffset:X}}
                    """);
                    WriteByteArray(libWriter, patchFileOffset, patchData.PatchBytes);
                }
            }

            RequireHash(LibFile, PatchedSha256, "patched runtime");
            Console.WriteLine($"Patched SHA-256: {GetFileHash(LibFile)}");
            Console.WriteLine($"Successfully patched {ArchName} runtime");
        }
        catch {
            Revert();
            throw;
        }
    }

    public void Revert() {
        var bakFile = LibFile + ".bak";

        if (File.Exists(bakFile)) {
            RequireHash(bakFile, OriginalSha256, "backup");
            File.Copy(bakFile, LibFile, true);
            File.Delete(bakFile);
            Console.WriteLine($"Reverted {bakFile}");
        }
        else {
            Console.WriteLine($"No backup file exists: {bakFile}");
        }
    }

    T GetFunctionOffsetVASection(SymbolEntry<T> func) {
        var section = func.PointedSection;
        if (section == null || ulong.CreateChecked(func.Value) < ulong.CreateChecked(section.LoadAddress))
            throw new InvalidDataException($"Invalid ELF section address for {func.Name}.");
        return func.Value - section.LoadAddress;
    }
    SymbolEntry<T> GetFunction(string name) {
        return MonoMethodMap[name];
    }
    T GetFunctionOffsetVAFile(string name) {
        var func = GetFunction(name);
        var offsetOnSection = GetFunctionOffsetVASection(func);
        return checked(func.PointedSection.Offset + offsetOnSection);
    }
    void WriteByteArray(FileStream file, long start, byte[] bytes) {
        file.Seek(start, SeekOrigin.Begin);
        file.Write(bytes);
    }

    static string GetFileHash(string path) {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }

    static void RequireHash(string path, string? expected, string description) {
        if (expected == null) return;
        var actual = GetFileHash(path);
        if (actual != expected)
            throw new InvalidDataException($"Unsupported {description}: {path}. Expected SHA-256 {expected}, found {actual}.");
    }

    protected virtual string? OriginalSha256 => null;
    protected virtual string? PatchedSha256 => null;
    protected virtual void ValidateLibraryHeader() { }
    protected virtual void ValidatePatchBytes(PatchData patch, long offset) { }
    protected abstract string ArchName { get; }
    protected abstract PatchData Patch_FieldAccessException();
    protected abstract PatchData Patch_MethodAccessException();
}
