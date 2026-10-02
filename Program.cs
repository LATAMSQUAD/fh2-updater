using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ActualizadorFH2
{
    // Aplica el paquete que viaja al final de este exe.
    // Servidor: reemplaza archivos enteros. Cliente: cambia archivos dentro de los zip.
    // Las versiones salen del indice del paquete.
    // Formato al final del exe (o de un .dat si no cupiera):
    //   datos de cada archivo
    //   indice
    //   pie de 24 bytes: "FH2UPD02", posicion del indice, largo del indice
    static class Program
    {
        const string Magic = "FH2UPD02";
        public const int ModeFull = 1;
        public const int ModeRebuild = 2;
        public const int ModeZipPatch = 3;

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--probar")
                return SelfTest();
            if (args.Length > 0 && args[0] == "--verificar")
                return Verify(PackagePath());
            if (args.Length > 1 && args[0] == "--aplicar")
                return ApplyCli(args[1]);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        static string ExePath()
        {
            return Assembly.GetExecutingAssembly().Location;
        }

        static string AppDir()
        {
            return Path.GetDirectoryName(ExePath());
        }

        // Si el propio exe trae el paquete, se usa ese. Si no, busca el .dat de al lado.
        public static string PackagePath()
        {
            string exe = ExePath();
            if (HasMagic(exe))
                return exe;
            string dat = Path.ChangeExtension(exe, ".dat");
            if (File.Exists(dat))
                return dat;
            return exe;
        }

        static bool HasMagic(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (stream.Length < 24)
                        return false;
                    stream.Seek(-24, SeekOrigin.End);
                    byte[] magic = new byte[8];
                    if (stream.Read(magic, 0, 8) != 8)
                        return false;
                    return Encoding.ASCII.GetString(magic) == Magic;
                }
            }
            catch (IOException)
            {
                return false;
            }
        }

        static int Verify(string path)
        {
            Package package;
            string error;
            var report = new StringBuilder();
            int code = 1;
            if (!Package.TryRead(path, out package, out error))
            {
                report.AppendLine("FAIL");
                report.AppendLine(error);
            }
            else
            {
                code = 0;
                report.AppendLine("OK");
                report.AppendLine("Desde: " + package.FromVersion);
                report.AppendLine("Hasta: " + package.ToVersion);
                report.AppendLine("Archivos: " + package.Files.Count);
                report.AppendLine("Borrar: " + package.DeleteFiles.Count);
                report.AppendLine("Cambios: " + package.CarriedBytes);
            }
            File.WriteAllText(Path.Combine(AppDir(), "verificacion.txt"), report.ToString(), Encoding.UTF8);
            return code;
        }

        static int ApplyCli(string destination)
        {
            string packagePath = PackagePath();
            Package package;
            string error;
            if (!Package.TryRead(packagePath, out package, out error))
                return Fail(error);
            var patcher = new Patcher();
            PatchResult result = patcher.Apply(packagePath, package, destination);
            if (result.Cancelled || result.Errors != 0)
                return Fail("errors=" + result.Errors);
            return 0;
        }

        // Reconstruye un archivo de prueba a partir del viejo y comprueba que queda igual al nuevo.
        static int SelfTest()
        {
            string baseDir = Path.Combine(Path.GetTempPath(), "fh2-upd-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                string packed = Path.Combine(baseDir, "mini.exe");
                string dest = Path.Combine(baseDir, "destino");
                Directory.CreateDirectory(dest);
                byte[] oldZip = Encoding.ASCII.GetBytes("0123456789abcdefghij");
                byte[] expected = Encoding.ASCII.GetBytes("abcdefghijHELLO01234");
                File.WriteAllBytes(Path.Combine(dest, "mapa.zip"), oldZip);
                File.SetAttributes(Path.Combine(dest, "mapa.zip"), FileAttributes.ReadOnly);
                File.WriteAllText(Path.Combine(dest, "viejo.txt"), "x");
                File.WriteAllBytes(packed, new byte[] { 0x4D, 0x5A });

                var files = new List<PackageFile>();
                files.Add(PackageWriter.WriteFull(packed, "hola.txt", Encoding.UTF8.GetBytes("hola")));
                files.Add(PackageWriter.WriteRebuild(packed, "mapa.zip", expected.Length, Crc32.Compute(expected),
                    new RebuildOp(2, 10, 10, null),
                    new RebuildOp(1, 5, 0, Encoding.ASCII.GetBytes("HELLO")),
                    new RebuildOp(2, 0, 5, null)));
                PackageWriter.Finish(packed, "5.6.507", "5.5.512", files, new[] { "viejo.txt" });

                Package package;
                string error;
                if (!Package.TryRead(packed, out package, out error))
                    return Fail("read " + error);

                var patcher = new Patcher();
                PatchResult result = patcher.Apply(packed, package, dest);
                byte[] got = File.ReadAllBytes(Path.Combine(dest, "mapa.zip"));
                bool same = got.Length == expected.Length;
                if (same)
                {
                    for (int i = 0; i < got.Length; i++)
                    {
                        if (got[i] != expected[i])
                            same = false;
                    }
                }
                bool content = same
                    && File.ReadAllText(Path.Combine(dest, "hola.txt")) == "hola"
                    && !File.Exists(Path.Combine(dest, "viejo.txt"));
                bool copied = result.Copied == 2 && result.Deleted == 1 && result.Errors == 0 && content;
                string outside;
                bool blocked = !Patcher.TryMap(dest, "..\\afuera.txt", out outside);
                bool zipOk = RunZipPatchTest(baseDir);
                string status = copied && blocked && zipOk ? "OK" : "FAIL";
                File.WriteAllText(Path.Combine(AppDir(), "probar-resultado.txt"),
                    status + " copied=" + copied + " blocked=" + blocked + " errors=" + result.Errors, Encoding.UTF8);
                return status == "OK" ? 0 : 1;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(baseDir))
                        Directory.Delete(baseDir, true);
                }
                catch (IOException)
                {
                }
            }
        }

        static bool RunZipPatchTest(string baseDir)
        {
            string packed = Path.Combine(baseDir, "zip.exe");
            string dest = Path.Combine(baseDir, "zipdest");
            Directory.CreateDirectory(dest);
            File.WriteAllBytes(Path.Combine(dest, "piezas.zip"), MakeStoredZip(
                new[] { "keep.txt", "old.txt", "gone.txt" },
                new[] { Encoding.ASCII.GetBytes("KEEP"), Encoding.ASCII.GetBytes("OLD!"), Encoding.ASCII.GetBytes("GONE") }));
            File.WriteAllBytes(packed, new byte[] { 0x4D, 0x5A });
            byte[] blob = BuildZipPatchBlob(
                "old.txt", Encoding.ASCII.GetBytes("NEW!"),
                "add.txt", Encoding.ASCII.GetBytes("ADD!"),
                "gone.txt");
            var file = new PackageFile();
            file.RelativePath = "piezas.zip";
            file.Mode = ModeZipPatch;
            file.OutputSize = 0;
            file.Crc = Crc32.Compute(blob);
            using (var stream = new FileStream(packed, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                file.Offset = stream.Length;
                stream.Write(blob, 0, blob.Length);
                file.BlobLength = blob.Length;
            }
            PackageWriter.Finish(packed, "5.6.586", "5.5.512", new[] { file }, new string[0]);
            Package package;
            string error;
            if (!Package.TryRead(packed, out package, out error))
                return false;
            var patcher = new Patcher();
            PatchResult result = patcher.Apply(packed, package, dest);
            if (result.Errors != 0 || result.Copied != 1)
                return false;
            string keep, replaced, added;
            if (!ReadStoredZipText(Path.Combine(dest, "piezas.zip"), "keep.txt", out keep))
                return false;
            if (!ReadStoredZipText(Path.Combine(dest, "piezas.zip"), "old.txt", out replaced))
                return false;
            if (!ReadStoredZipText(Path.Combine(dest, "piezas.zip"), "add.txt", out added))
                return false;
            byte[] gone;
            bool removed = !Patcher.TryReadZipEntry(Path.Combine(dest, "piezas.zip"), "gone.txt", out gone);
            return keep == "KEEP" && replaced == "NEW!" && added == "ADD!" && removed;
        }

        static byte[] BuildZipPatchBlob(string replaceName, byte[] replaceData, string addName, byte[] addData, string deleteName)
        {
            var blob = new MemoryStream();
            WriteU32(blob, 3);
            WriteZipPut(blob, replaceName, replaceData);
            WriteZipPut(blob, addName, addData);
            WriteZipDelete(blob, deleteName);
            return blob.ToArray();
        }

        static void WriteZipPut(Stream blob, string name, byte[] data)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            blob.WriteByte(1);
            WriteU16(blob, (ushort)nameBytes.Length);
            blob.Write(nameBytes, 0, nameBytes.Length);
            WriteU16(blob, 0);
            WriteU16(blob, 0);
            WriteU16(blob, 0);
            WriteU16(blob, 0);
            WriteU32(blob, Crc32.Compute(data));
            WriteU32(blob, (uint)data.Length);
            WriteU32(blob, (uint)data.Length);
            blob.Write(data, 0, data.Length);
        }

        static void WriteZipDelete(Stream blob, string name)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            blob.WriteByte(2);
            WriteU16(blob, (ushort)nameBytes.Length);
            blob.Write(nameBytes, 0, nameBytes.Length);
        }

        internal static void WriteU16(Stream stream, ushort value)
        {
            stream.WriteByte((byte)(value & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
        }

        internal static void WriteU32(Stream stream, uint value)
        {
            stream.WriteByte((byte)(value & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
            stream.WriteByte((byte)((value >> 16) & 0xFF));
            stream.WriteByte((byte)((value >> 24) & 0xFF));
        }

        static byte[] MakeStoredZip(string[] names, byte[][] datas)
        {
            var locals = new MemoryStream();
            var central = new MemoryStream();
            for (int i = 0; i < names.Length; i++)
            {
                byte[] name = Encoding.UTF8.GetBytes(names[i]);
                uint crc = Crc32.Compute(datas[i]);
                long localOffset = locals.Length;
                WriteU32(locals, 0x04034b50);
                WriteU16(locals, 20);
                WriteU16(locals, 0);
                WriteU16(locals, 0);
                WriteU16(locals, 0);
                WriteU16(locals, 0);
                WriteU32(locals, crc);
                WriteU32(locals, (uint)datas[i].Length);
                WriteU32(locals, (uint)datas[i].Length);
                WriteU16(locals, (ushort)name.Length);
                WriteU16(locals, 0);
                locals.Write(name, 0, name.Length);
                locals.Write(datas[i], 0, datas[i].Length);
                WriteU32(central, 0x02014b50);
                WriteU16(central, 20);
                WriteU16(central, 20);
                WriteU16(central, 0);
                WriteU16(central, 0);
                WriteU16(central, 0);
                WriteU16(central, 0);
                WriteU32(central, crc);
                WriteU32(central, (uint)datas[i].Length);
                WriteU32(central, (uint)datas[i].Length);
                WriteU16(central, (ushort)name.Length);
                WriteU16(central, 0);
                WriteU16(central, 0);
                WriteU16(central, 0);
                WriteU16(central, 0);
                WriteU32(central, 0);
                WriteU32(central, (uint)localOffset);
                central.Write(name, 0, name.Length);
            }
            var zip = new MemoryStream();
            byte[] localBytes = locals.ToArray();
            byte[] centralBytes = central.ToArray();
            zip.Write(localBytes, 0, localBytes.Length);
            zip.Write(centralBytes, 0, centralBytes.Length);
            WriteU32(zip, 0x06054b50);
            WriteU16(zip, 0);
            WriteU16(zip, 0);
            WriteU16(zip, (ushort)names.Length);
            WriteU16(zip, (ushort)names.Length);
            WriteU32(zip, (uint)centralBytes.Length);
            WriteU32(zip, (uint)localBytes.Length);
            WriteU16(zip, 0);
            return zip.ToArray();
        }

        static bool ReadStoredZipText(string path, string name, out string text)
        {
            text = null;
            byte[] data;
            if (!Patcher.TryReadZipEntry(path, name, out data))
                return false;
            text = Encoding.ASCII.GetString(data);
            return true;
        }

        static int Fail(string message)
        {
            File.WriteAllText(Path.Combine(AppDir(), "probar-resultado.txt"), "FAIL " + message, Encoding.UTF8);
            return 1;
        }
    }

    sealed class RebuildOp
    {
        public int Kind;
        public long A;
        public long B;
        public byte[] Literal;

        public RebuildOp(int kind, long a, long b, byte[] literal)
        {
            Kind = kind;
            A = a;
            B = b;
            Literal = literal;
        }
    }

    sealed class PackageFile
    {
        public string RelativePath;
        public int Mode;
        public long OutputSize;
        public long Offset;
        public long BlobLength;
        public uint Crc;
    }

    sealed class Package
    {
        public string ToVersion;
        public string FromVersion;
        public List<PackageFile> Files = new List<PackageFile>();
        public List<string> DeleteFiles = new List<string>();
        public long CarriedBytes;

        public static bool TryRead(string path, out Package package, out string error)
        {
            package = null;
            error = null;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (stream.Length < 24)
                    {
                        error = "El programa no contiene la actualización.";
                        return false;
                    }
                    stream.Seek(-24, SeekOrigin.End);
                    byte[] magic = ReadExact(stream, 8);
                    if (Encoding.ASCII.GetString(magic) != "FH2UPD02")
                    {
                        error = "El programa no contiene la actualización.";
                        return false;
                    }
                    long indexOffset = ReadInt64(stream);
                    long indexLength = ReadInt64(stream);
                    if (indexLength < 8 || indexLength > 80L * 1024L * 1024L)
                    {
                        error = "El índice de la actualización está dañado.";
                        return false;
                    }
                    if (indexOffset < 0 || indexOffset + indexLength != stream.Length - 24)
                    {
                        error = "El índice de la actualización no cierra con el archivo.";
                        return false;
                    }

                    stream.Seek(indexOffset, SeekOrigin.Begin);
                    byte[] index = ReadExact(stream, indexLength);
                    var reader = new MemoryStream(index);
                    var result = new Package();
                    result.ToVersion = ReadString(reader);
                    result.FromVersion = ReadString(reader);
                    int count = ReadInt32(reader);
                    if (count < 0 || count > 100000)
                    {
                        error = "La cantidad de archivos no es válida.";
                        return false;
                    }
                    long cursor = 0;
                    for (int i = 0; i < count; i++)
                    {
                        var file = new PackageFile();
                        file.RelativePath = ReadString(reader);
                        file.Mode = ReadInt32(reader);
                        file.OutputSize = ReadInt64(reader);
                        file.Offset = ReadInt64(reader);
                        file.BlobLength = ReadInt64(reader);
                        file.Crc = ReadUInt32(reader);
                        if (file.Mode != Program.ModeFull && file.Mode != Program.ModeRebuild && file.Mode != Program.ModeZipPatch)
                        {
                            error = "Tipo de archivo desconocido: " + file.RelativePath;
                            return false;
                        }
                        if (file.OutputSize < 0 || file.BlobLength < 0 || file.Offset < 0 || file.Offset + file.BlobLength > indexOffset)
                        {
                            error = "Archivo fuera de rango: " + file.RelativePath;
                            return false;
                        }
                        if (file.Offset < cursor)
                        {
                            error = "Datos superpuestos: " + file.RelativePath;
                            return false;
                        }
                        cursor = file.Offset + file.BlobLength;
                        result.Files.Add(file);
                        result.CarriedBytes += file.BlobLength;
                    }
                    int deleteCount = ReadInt32(reader);
                    if (deleteCount < 0 || deleteCount > 100000)
                    {
                        error = "La lista de borrados no es válida.";
                        return false;
                    }
                    for (int i = 0; i < deleteCount; i++)
                        result.DeleteFiles.Add(ReadString(reader));
                    if (reader.Position != reader.Length)
                    {
                        error = "El índice tiene datos de más.";
                        return false;
                    }
                    package = result;
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static string ReadString(Stream stream)
        {
            int length = ReadInt32(stream);
            if (length < 0 || length > 4096)
                throw new InvalidDataException("Texto de largo inválido.");
            if (length == 0)
                return "";
            return Encoding.UTF8.GetString(ReadExact(stream, length));
        }

        public static byte[] ReadExact(Stream stream, long count)
        {
            if (count > int.MaxValue)
                throw new InvalidDataException("Bloque demasiado grande.");
            byte[] data = new byte[count];
            int offset = 0;
            while (offset < data.Length)
            {
                int read = stream.Read(data, offset, data.Length - offset);
                if (read <= 0)
                    throw new EndOfStreamException("Archivo incompleto.");
                offset += read;
            }
            return data;
        }

        public static int ReadInt32(Stream stream)
        {
            byte[] data = ReadExact(stream, 4);
            return data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24);
        }

        public static uint ReadUInt32(Stream stream)
        {
            return (uint)ReadInt32(stream);
        }

        public static long ReadInt64(Stream stream)
        {
            byte[] data = ReadExact(stream, 8);
            uint low = (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));
            uint high = (uint)(data[4] | (data[5] << 8) | (data[6] << 16) | (data[7] << 24));
            return (long)low | ((long)high << 32);
        }
    }

    static class PackageWriter
    {
        public static PackageFile WriteFull(string packagePath, string relativePath, byte[] content)
        {
            var file = new PackageFile();
            file.RelativePath = relativePath;
            file.Mode = Program.ModeFull;
            file.OutputSize = content.Length;
            file.BlobLength = content.Length;
            file.Crc = Crc32.Compute(content);
            using (var stream = new FileStream(packagePath, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                file.Offset = stream.Length;
                stream.Seek(0, SeekOrigin.End);
                stream.Write(content, 0, content.Length);
            }
            return file;
        }

        public static PackageFile WriteRebuild(string packagePath, string relativePath, long outputSize, uint crc, params RebuildOp[] ops)
        {
            var file = new PackageFile();
            file.RelativePath = relativePath;
            file.Mode = Program.ModeRebuild;
            file.OutputSize = outputSize;
            file.Crc = crc;
            using (var stream = new FileStream(packagePath, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                file.Offset = stream.Length;
                stream.Seek(0, SeekOrigin.End);
                WriteInt32(stream, ops.Length);
                foreach (RebuildOp op in ops)
                {
                    stream.WriteByte((byte)op.Kind);
                    WriteInt64(stream, op.A);
                    WriteInt64(stream, op.B);
                    if (op.Kind == 1)
                    {
                        if (op.Literal == null || op.Literal.Length != op.A)
                            throw new InvalidOperationException("Literal invalido.");
                        stream.Write(op.Literal, 0, op.Literal.Length);
                    }
                }
                file.BlobLength = stream.Length - file.Offset;
            }
            return file;
        }

        public static void Finish(string packagePath, string toVersion, string fromVersion, IList<PackageFile> files, IList<string> deleteFiles)
        {
            using (var stream = new FileStream(packagePath, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                stream.Seek(0, SeekOrigin.End);
                long indexOffset = stream.Length;
                var index = new MemoryStream();
                WriteString(index, toVersion);
                WriteString(index, fromVersion);
                WriteInt32(index, files.Count);
                foreach (PackageFile file in files)
                {
                    WriteString(index, file.RelativePath);
                    WriteInt32(index, file.Mode);
                    WriteInt64(index, file.OutputSize);
                    WriteInt64(index, file.Offset);
                    WriteInt64(index, file.BlobLength);
                    WriteUInt32(index, file.Crc);
                }
                WriteInt32(index, deleteFiles.Count);
                foreach (string rel in deleteFiles)
                    WriteString(index, rel);
                byte[] indexBytes = index.ToArray();
                stream.Write(indexBytes, 0, indexBytes.Length);
                stream.Write(Encoding.ASCII.GetBytes("FH2UPD02"), 0, 8);
                WriteInt64(stream, indexOffset);
                WriteInt64(stream, indexBytes.Length);
            }
        }

        static void WriteString(Stream stream, string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text ?? "");
            WriteInt32(stream, data.Length);
            stream.Write(data, 0, data.Length);
        }

        public static void WriteInt32(Stream stream, int value)
        {
            stream.WriteByte((byte)value);
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 24));
        }

        static void WriteUInt32(Stream stream, uint value)
        {
            WriteInt32(stream, (int)value);
        }

        public static void WriteInt64(Stream stream, long value)
        {
            WriteInt32(stream, (int)value);
            WriteInt32(stream, (int)(value >> 32));
        }
    }

    static class Crc32
    {
        static readonly uint[] Table = BuildTable();

        public static uint Compute(byte[] data)
        {
            return Finish(Update(Start(), data, data.Length));
        }

        public static uint Start()
        {
            return 0xFFFFFFFFu;
        }

        public static uint Update(uint crc, byte[] data, int count)
        {
            for (int i = 0; i < count; i++)
                crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        public static uint Finish(uint crc)
        {
            return crc ^ 0xFFFFFFFFu;
        }

        static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }
    }

    sealed class PatchResult
    {
        public int Copied;
        public int Deleted;
        public int Errors;
        public bool Cancelled;
    }

    sealed class Patcher
    {
        public Func<bool> CancelRequested;
        public Action<string> OnLog;
        public Action<int, int, string> OnProgress;

        public PatchResult Apply(string packagePath, Package package, string destination)
        {
            var result = new PatchResult();
            int total = package.Files.Count + package.DeleteFiles.Count;
            int done = 0;
            byte[] buffer = new byte[1024 * 1024];

            using (var pack = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                foreach (PackageFile file in package.Files)
                {
                    if (IsCancelled())
                    {
                        result.Cancelled = true;
                        Log("Actualización detenida.");
                        return result;
                    }
                    Report(done, total, file.RelativePath);
                    string destFile;
                    if (!TryMap(destination, file.RelativePath, out destFile))
                    {
                        result.Errors++;
                        Log("Ruta rechazada: " + file.RelativePath);
                        done++;
                        continue;
                    }
                    string tempFile = destFile + ".fh2new";
                    try
                    {
                        string folder = Path.GetDirectoryName(destFile);
                        if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                            Directory.CreateDirectory(folder);
                        if (File.Exists(tempFile))
                        {
                            ClearReadOnly(tempFile);
                            File.Delete(tempFile);
                        }
                        uint crc = Crc32.Start();
                        if (file.Mode == Program.ModeFull)
                            crc = WriteFull(pack, file, tempFile, buffer, crc);
                        else if (file.Mode == Program.ModeRebuild)
                            crc = WriteRebuild(pack, file, destFile, tempFile, buffer, crc);
                        else if (file.Mode == Program.ModeZipPatch)
                            crc = WriteZipPatch(pack, file, destFile, tempFile, buffer, crc);
                        else
                            throw new InvalidDataException("Tipo de archivo desconocido.");
                        if (Crc32.Finish(crc) != file.Crc)
                        {
                            result.Errors++;
                            Log("El archivo no coincide y se dejó el anterior: " + file.RelativePath);
                            DeleteQuiet(tempFile);
                        }
                        else
                        {
                            ReplaceFile(tempFile, destFile);
                            result.Copied++;
                            Log("Actualizado: " + file.RelativePath);
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Errors++;
                        Log("Error al actualizar " + file.RelativePath + ": " + ex.Message);
                        DeleteQuiet(tempFile);
                    }
                    done++;
                }
            }

            foreach (string rel in package.DeleteFiles)
            {
                if (IsCancelled())
                {
                    result.Cancelled = true;
                    Log("Actualización detenida.");
                    return result;
                }
                Report(done, total, rel);
                string destFile;
                if (!TryMap(destination, rel, out destFile))
                {
                    result.Errors++;
                    Log("Ruta rechazada: " + rel);
                    done++;
                    continue;
                }
                try
                {
                    if (File.Exists(destFile))
                    {
                        ClearReadOnly(destFile);
                        File.Delete(destFile);
                        result.Deleted++;
                        Log("Borrado: " + rel);
                    }
                    else
                    {
                        Log("Ya no estaba: " + rel);
                    }
                }
                catch (Exception ex)
                {
                    result.Errors++;
                    Log("Error al borrar " + rel + ": " + ex.Message);
                }
                done++;
            }

            Report(total, total, "Listo");
            return result;
        }

        static uint WriteFull(FileStream pack, PackageFile file, string tempFile, byte[] buffer, uint crc)
        {
            pack.Seek(file.Offset, SeekOrigin.Begin);
            long left = file.BlobLength;
            using (var output = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                while (left > 0)
                {
                    int want = (int)Math.Min(buffer.Length, left);
                    int read = pack.Read(buffer, 0, want);
                    if (read <= 0)
                        throw new EndOfStreamException("Archivo incompleto dentro del programa.");
                    output.Write(buffer, 0, read);
                    crc = Crc32.Update(crc, buffer, read);
                    left -= read;
                }
            }
            return crc;
        }

        static uint WriteRebuild(FileStream pack, PackageFile file, string destFile, string tempFile, byte[] buffer, uint crc)
        {
            if (!File.Exists(destFile))
                throw new FileNotFoundException("Falta el archivo de la versión anterior.", destFile);
            pack.Seek(file.Offset, SeekOrigin.Begin);
            long blobLeft = file.BlobLength;
            int opCount = ReadCheckedInt(pack, ref blobLeft);
            using (var oldFile = new FileStream(destFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var output = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                long written = 0;
                for (int i = 0; i < opCount; i++)
                {
                    int kind = ReadCheckedByte(pack, ref blobLeft);
                    long a = ReadCheckedInt64(pack, ref blobLeft);
                    long b = ReadCheckedInt64(pack, ref blobLeft);
                    if (kind == 1)
                    {
                        if (a < 0)
                            throw new InvalidDataException("Bloque invalido.");
                        long left = a;
                        while (left > 0)
                        {
                            int want = (int)Math.Min(buffer.Length, left);
                            int read = pack.Read(buffer, 0, want);
                            if (read <= 0)
                                throw new EndOfStreamException("Bloque incompleto.");
                            blobLeft -= read;
                            output.Write(buffer, 0, read);
                            crc = Crc32.Update(crc, buffer, read);
                            written += read;
                            left -= read;
                        }
                    }
                    else if (kind == 2)
                    {
                        if (a < 0 || b < 0 || a + b > oldFile.Length)
                            throw new InvalidDataException("El archivo anterior no coincide con la versión de origen.");
                        oldFile.Seek(a, SeekOrigin.Begin);
                        long left = b;
                        while (left > 0)
                        {
                            int want = (int)Math.Min(buffer.Length, left);
                            int read = oldFile.Read(buffer, 0, want);
                            if (read <= 0)
                                throw new EndOfStreamException("No se pudo leer el archivo anterior.");
                            output.Write(buffer, 0, read);
                            crc = Crc32.Update(crc, buffer, read);
                            written += read;
                            left -= read;
                        }
                    }
                    else
                    {
                        throw new InvalidDataException("Operación desconocida.");
                    }
                }
                if (blobLeft != 0 || written != file.OutputSize)
                    throw new InvalidDataException("El paquete de " + file.RelativePath + " no cierra.");
            }
            return crc;
        }

        static int ReadCheckedByte(FileStream pack, ref long blobLeft)
        {
            if (blobLeft < 1)
                throw new EndOfStreamException("Paquete truncado.");
            int value = pack.ReadByte();
            if (value < 0)
                throw new EndOfStreamException("Paquete truncado.");
            blobLeft -= 1;
            return value;
        }

        static int ReadCheckedInt(FileStream pack, ref long blobLeft)
        {
            byte[] data = ReadChecked(pack, 4, ref blobLeft);
            return data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24);
        }

        static long ReadCheckedInt64(FileStream pack, ref long blobLeft)
        {
            byte[] data = ReadChecked(pack, 8, ref blobLeft);
            uint low = (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));
            uint high = (uint)(data[4] | (data[5] << 8) | (data[6] << 16) | (data[7] << 24));
            return (long)low | ((long)high << 32);
        }

        static byte[] ReadChecked(FileStream pack, int count, ref long blobLeft)
        {
            if (blobLeft < count)
                throw new EndOfStreamException("Paquete truncado.");
            byte[] data = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = pack.Read(data, offset, count - offset);
                if (read <= 0)
                    throw new EndOfStreamException("Paquete truncado.");
                offset += read;
            }
            blobLeft -= count;
            return data;
        }

        // Cambia archivos dentro de un zip que el cliente ya tiene.
        // El blob trae altas, reemplazos y bajas. Lo demas se copia tal cual.
        static uint WriteZipPatch(FileStream pack, PackageFile file, string destFile, string tempFile, byte[] buffer, uint crc)
        {
            if (!File.Exists(destFile))
                throw new FileNotFoundException("Falta el zip de la versión anterior.", destFile);
            pack.Seek(file.Offset, SeekOrigin.Begin);
            long left = file.BlobLength;
            List<ZipOp> ops = ReadZipOps(pack, ref left, ref crc, buffer);
            if (left != 0)
                throw new InvalidDataException("El paquete de " + file.RelativePath + " no cierra.");
            RewriteZip(destFile, tempFile, ops, pack, buffer);
            return crc;
        }

        sealed class ZipOp
        {
            public int Kind;
            public string Name;
            public ushort Method;
            public ushort Flags;
            public ushort Time;
            public ushort Date;
            public uint Crc;
            public uint CompSize;
            public uint UncompSize;
            public long DataPos;
        }

        sealed class ZipEntry
        {
            public string Name;
            public byte[] NameBytes;
            public ushort Method;
            public ushort Flags;
            public ushort Time;
            public ushort Date;
            public uint Crc;
            public uint CompSize;
            public uint UncompSize;
            public long DataOffset;
            public bool Delete;
            public ZipOp Replacement;
        }

        static List<ZipOp> ReadZipOps(FileStream pack, ref long left, ref uint crc, byte[] buffer)
        {
            int count = ReadCrcInt(pack, ref left, ref crc);
            if (count < 0 || count > 200000)
                throw new InvalidDataException("Demasiados cambios dentro del zip.");
            var ops = new List<ZipOp>();
            for (int i = 0; i < count; i++)
            {
                int kind = ReadCrcByte(pack, ref left, ref crc);
                int nameLen = ReadCrcU16(pack, ref left, ref crc);
                if (nameLen <= 0 || nameLen > 1024)
                    throw new InvalidDataException("Nombre interno invalido.");
                byte[] nameBytes = ReadCrcBytes(pack, nameLen, ref left, ref crc, buffer);
                var op = new ZipOp();
                op.Kind = kind;
                op.Name = Encoding.UTF8.GetString(nameBytes).Replace('\\', '/');
                if (kind == 2)
                {
                    ops.Add(op);
                    continue;
                }
                if (kind != 1)
                    throw new InvalidDataException("Cambio de zip desconocido.");
                op.Method = (ushort)ReadCrcU16(pack, ref left, ref crc);
                op.Flags = (ushort)ReadCrcU16(pack, ref left, ref crc);
                op.Time = (ushort)ReadCrcU16(pack, ref left, ref crc);
                op.Date = (ushort)ReadCrcU16(pack, ref left, ref crc);
                op.Crc = (uint)ReadCrcInt(pack, ref left, ref crc);
                op.CompSize = (uint)ReadCrcInt(pack, ref left, ref crc);
                op.UncompSize = (uint)ReadCrcInt(pack, ref left, ref crc);
                if (op.CompSize > int.MaxValue)
                    throw new InvalidDataException("Archivo interno demasiado grande.");
                op.DataPos = pack.Position;
                SkipCrc(pack, op.CompSize, ref left, ref crc, buffer);
                ops.Add(op);
            }
            return ops;
        }

        static void RewriteZip(string destFile, string tempFile, List<ZipOp> ops, FileStream pack, byte[] buffer)
        {
            var entries = new List<ZipEntry>();
            var byName = new Dictionary<string, ZipEntry>(StringComparer.OrdinalIgnoreCase);
            using (var oldZip = new FileStream(destFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                ReadCentralDirectory(oldZip, entries, buffer);
                foreach (ZipEntry entry in entries)
                    byName[entry.Name] = entry;
                var added = new List<ZipOp>();
                foreach (ZipOp op in ops)
                {
                    ZipEntry entry;
                    if (!byName.TryGetValue(op.Name, out entry))
                    {
                        if (op.Kind == 1)
                            added.Add(op);
                        continue;
                    }
                    if (op.Kind == 2)
                        entry.Delete = true;
                    else
                        entry.Replacement = op;
                }
                using (var output = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var written = new List<ZipEntry>();
                    foreach (ZipEntry entry in entries)
                    {
                        if (entry.Delete)
                            continue;
                        entry.DataOffset = output.Position;
                        if (entry.Replacement != null)
                            WriteReplaced(output, entry, pack, buffer);
                        else
                            WriteCopied(output, entry, oldZip, buffer);
                        written.Add(entry);
                    }
                    foreach (ZipOp op in added)
                    {
                        var entry = new ZipEntry();
                        entry.Name = op.Name;
                        entry.NameBytes = Encoding.UTF8.GetBytes(op.Name);
                        entry.Replacement = op;
                        entry.DataOffset = output.Position;
                        WriteReplaced(output, entry, pack, buffer);
                        written.Add(entry);
                    }
                    if (written.Count > 65535)
                        throw new InvalidDataException("El zip tiene demasiados archivos internos.");
                    long centralOffset = output.Position;
                    foreach (ZipEntry entry in written)
                        WriteCentral(output, entry);
                    long centralSize = output.Position - centralOffset;
                    if (centralOffset > uint.MaxValue || centralSize > uint.MaxValue)
                        throw new InvalidDataException("El zip quedó demasiado grande.");
                    WriteU32(output, 0x06054b50);
                    WriteU16(output, 0);
                    WriteU16(output, 0);
                    WriteU16(output, (ushort)written.Count);
                    WriteU16(output, (ushort)written.Count);
                    WriteU32(output, (uint)centralSize);
                    WriteU32(output, (uint)centralOffset);
                    WriteU16(output, 0);
                }
            }
        }

        static void ReadCentralDirectory(FileStream zip, List<ZipEntry> entries, byte[] buffer)
        {
            long eocd = FindEocd(zip, buffer);
            zip.Seek(eocd + 10, SeekOrigin.Begin);
            int count = ReadU16(zip);
            uint centralSize = ReadU32(zip);
            uint centralOffset = ReadU32(zip);
            if (centralOffset == 0xFFFFFFFF || centralSize == 0xFFFFFFFF)
                throw new InvalidDataException("Ese zip usa un formato que este actualizador no puede rearmar.");
            zip.Seek(centralOffset, SeekOrigin.Begin);
            long end = centralOffset + centralSize;
            for (int i = 0; i < count; i++)
            {
                if (zip.Position + 46 > end)
                    throw new InvalidDataException("El indice del zip está dañado.");
                uint sig = ReadU32(zip);
                if (sig != 0x02014b50)
                    throw new InvalidDataException("El indice del zip está dañado.");
                zip.Seek(4, SeekOrigin.Current);
                var entry = new ZipEntry();
                entry.Flags = ReadU16(zip);
                entry.Method = ReadU16(zip);
                entry.Time = ReadU16(zip);
                entry.Date = ReadU16(zip);
                entry.Crc = ReadU32(zip);
                entry.CompSize = ReadU32(zip);
                entry.UncompSize = ReadU32(zip);
                int nameLen = ReadU16(zip);
                int extraLen = ReadU16(zip);
                int commentLen = ReadU16(zip);
                zip.Seek(8, SeekOrigin.Current);
                uint localOffset = ReadU32(zip);
                byte[] nameBytes = ReadExactSmall(zip, nameLen);
                if (extraLen > 0)
                    zip.Seek(extraLen, SeekOrigin.Current);
                if (commentLen > 0)
                    zip.Seek(commentLen, SeekOrigin.Current);
                entry.NameBytes = nameBytes;
                entry.Name = Encoding.UTF8.GetString(nameBytes).Replace('\\', '/');
                entry.DataOffset = LocalDataOffset(zip, localOffset);
                entries.Add(entry);
            }
        }

        static long LocalDataOffset(FileStream zip, uint localOffset)
        {
            long back = zip.Position;
            zip.Seek(localOffset, SeekOrigin.Begin);
            uint sig = ReadU32(zip);
            if (sig != 0x04034b50)
                throw new InvalidDataException("Cabecera de zip invalida.");
            zip.Seek(22, SeekOrigin.Current);
            int nameLen = ReadU16(zip);
            int extraLen = ReadU16(zip);
            long data = localOffset + 30 + nameLen + extraLen;
            zip.Seek(back, SeekOrigin.Begin);
            return data;
        }

        static long FindEocd(FileStream zip, byte[] buffer)
        {
            int scan = (int)Math.Min(zip.Length, 65557);
            if (scan < 22)
                throw new InvalidDataException("El archivo no es un zip.");
            zip.Seek(zip.Length - scan, SeekOrigin.Begin);
            byte[] tail = ReadExactSmall(zip, scan);
            for (int i = tail.Length - 22; i >= 0; i--)
            {
                if (tail[i] == 0x50 && tail[i + 1] == 0x4B && tail[i + 2] == 0x05 && tail[i + 3] == 0x06)
                    return zip.Length - scan + i;
            }
            throw new InvalidDataException("El archivo no es un zip.");
        }

        static void WriteCopied(Stream output, ZipEntry entry, FileStream oldZip, byte[] buffer)
        {
            WriteLocalHeader(output, entry.NameBytes, entry.Method, entry.Flags, entry.Time, entry.Date, entry.Crc, entry.CompSize, entry.UncompSize);
            oldZip.Seek(entry.DataOffset, SeekOrigin.Begin);
            CopyExact(oldZip, output, entry.CompSize, buffer);
        }

        static void WriteReplaced(Stream output, ZipEntry entry, FileStream pack, byte[] buffer)
        {
            ZipOp op = entry.Replacement;
            byte[] nameBytes = entry.NameBytes != null ? entry.NameBytes : Encoding.UTF8.GetBytes(op.Name);
            WriteLocalHeader(output, nameBytes, op.Method, op.Flags, op.Time, op.Date, op.Crc, op.CompSize, op.UncompSize);
            pack.Seek(op.DataPos, SeekOrigin.Begin);
            CopyExact(pack, output, op.CompSize, buffer);
            entry.Method = op.Method;
            entry.Flags = op.Flags;
            entry.Time = op.Time;
            entry.Date = op.Date;
            entry.Crc = op.Crc;
            entry.CompSize = op.CompSize;
            entry.UncompSize = op.UncompSize;
            entry.NameBytes = nameBytes;
        }

        static void WriteU16(Stream stream, ushort value)
        {
            Program.WriteU16(stream, value);
        }

        static void WriteU32(Stream stream, uint value)
        {
            Program.WriteU32(stream, value);
        }

        static void WriteLocalHeader(Stream output, byte[] name, ushort method, ushort flags, ushort time, ushort date, uint crc, uint comp, uint uncomp)
        {
            flags = (ushort)(flags & ~8);
            WriteU32(output, 0x04034b50);
            WriteU16(output, 20);
            WriteU16(output, flags);
            WriteU16(output, method);
            WriteU16(output, time);
            WriteU16(output, date);
            WriteU32(output, crc);
            WriteU32(output, comp);
            WriteU32(output, uncomp);
            WriteU16(output, (ushort)name.Length);
            WriteU16(output, 0);
            output.Write(name, 0, name.Length);
        }

        static void WriteCentral(Stream output, ZipEntry entry)
        {
            byte[] name = entry.NameBytes;
            WriteU32(output, 0x02014b50);
            WriteU16(output, 20);
            WriteU16(output, 20);
            WriteU16(output, (ushort)(entry.Flags & ~8));
            WriteU16(output, entry.Method);
            WriteU16(output, entry.Time);
            WriteU16(output, entry.Date);
            WriteU32(output, entry.Crc);
            WriteU32(output, entry.CompSize);
            WriteU32(output, entry.UncompSize);
            WriteU16(output, (ushort)name.Length);
            WriteU16(output, 0);
            WriteU16(output, 0);
            WriteU16(output, 0);
            WriteU16(output, 0);
            WriteU32(output, 0);
            WriteU32(output, (uint)entry.DataOffset);
            output.Write(name, 0, name.Length);
        }

        public static bool TryReadZipEntry(string path, string name, out byte[] data)
        {
            data = null;
            try
            {
                using (var zip = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var entries = new List<ZipEntry>();
                    ReadCentralDirectory(zip, entries, new byte[4096]);
                    foreach (ZipEntry entry in entries)
                    {
                        if (!string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (entry.Method != 0 || entry.UncompSize > int.MaxValue)
                            return false;
                        zip.Seek(entry.DataOffset, SeekOrigin.Begin);
                        data = ReadExactSmall(zip, (int)entry.UncompSize);
                        return true;
                    }
                }
            }
            catch (IOException)
            {
                return false;
            }
            catch (InvalidDataException)
            {
                return false;
            }
            return false;
        }

        static void CopyExact(Stream source, Stream dest, uint count, byte[] buffer)
        {
            long left = count;
            while (left > 0)
            {
                int want = (int)Math.Min(buffer.Length, left);
                int read = source.Read(buffer, 0, want);
                if (read <= 0)
                    throw new EndOfStreamException("Zip incompleto.");
                dest.Write(buffer, 0, read);
                left -= read;
            }
        }

        static int ReadCrcByte(FileStream pack, ref long left, ref uint crc)
        {
            byte[] data = ReadCrcBytes(pack, 1, ref left, ref crc, null);
            return data[0];
        }

        static int ReadCrcU16(FileStream pack, ref long left, ref uint crc)
        {
            byte[] data = ReadCrcBytes(pack, 2, ref left, ref crc, null);
            return data[0] | (data[1] << 8);
        }

        static int ReadCrcInt(FileStream pack, ref long left, ref uint crc)
        {
            byte[] data = ReadCrcBytes(pack, 4, ref left, ref crc, null);
            return data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24);
        }

        static byte[] ReadCrcBytes(FileStream pack, int count, ref long left, ref uint crc, byte[] buffer)
        {
            if (count < 0 || left < count)
                throw new EndOfStreamException("Paquete truncado.");
            byte[] data = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = pack.Read(data, offset, count - offset);
                if (read <= 0)
                    throw new EndOfStreamException("Paquete truncado.");
                offset += read;
            }
            left -= count;
            crc = Crc32.Update(crc, data, count);
            return data;
        }

        static void SkipCrc(FileStream pack, uint count, ref long left, ref uint crc, byte[] buffer)
        {
            long remaining = count;
            if (left < remaining)
                throw new EndOfStreamException("Paquete truncado.");
            while (remaining > 0)
            {
                int want = (int)Math.Min(buffer.Length, remaining);
                int read = pack.Read(buffer, 0, want);
                if (read <= 0)
                    throw new EndOfStreamException("Paquete truncado.");
                crc = Crc32.Update(crc, buffer, read);
                remaining -= read;
                left -= read;
            }
        }

        static byte[] ReadExactSmall(Stream stream, int count)
        {
            byte[] data = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(data, offset, count - offset);
                if (read <= 0)
                    throw new EndOfStreamException("Zip incompleto.");
                offset += read;
            }
            return data;
        }

        static ushort ReadU16(Stream stream)
        {
            byte[] data = ReadExactSmall(stream, 2);
            return (ushort)(data[0] | (data[1] << 8));
        }

        static uint ReadU32(Stream stream)
        {
            byte[] data = ReadExactSmall(stream, 4);
            return (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));
        }

        static void ReplaceFile(string tempFile, string destFile)
        {
            if (File.Exists(destFile))
            {
                ClearReadOnly(destFile);
                File.Replace(tempFile, destFile, null);
            }
            else
            {
                File.Move(tempFile, destFile);
            }
        }

        static void DeleteQuiet(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    ClearReadOnly(path);
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
        }

        public static bool TryMap(string root, string relative, out string fullPath)
        {
            fullPath = null;
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relative))
                return false;
            string rel = relative.Trim().Replace('/', '\\');
            if (Path.IsPathRooted(rel) || rel.IndexOf(':') >= 0)
                return false;
            string[] parts = rel.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return false;
            foreach (string part in parts)
            {
                if (part == "." || part == "..")
                    return false;
            }
            string rootFull = Path.GetFullPath(root);
            fullPath = Path.GetFullPath(Path.Combine(rootFull, string.Join("\\", parts)));
            string prefix = rootFull.TrimEnd('\\') + "\\";
            return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        bool IsCancelled()
        {
            return CancelRequested != null && CancelRequested();
        }

        void Log(string line)
        {
            if (OnLog != null)
                OnLog(line);
        }

        void Report(int done, int total, string name)
        {
            if (OnProgress != null)
                OnProgress(done, total, name);
        }

        static void ClearReadOnly(string path)
        {
            if (!File.Exists(path))
                return;
            FileAttributes attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
    }

    // Registro sin barras blancas. La rueda del mouse sigue moviendo el texto.
    sealed class LogBox : TextBox
    {
        const int WM_MOUSEWHEEL = 0x020A;
        const int EM_LINESCROLL = 0x00B6;

        public LogBox()
        {
            Multiline = true;
            ReadOnly = true;
            ScrollBars = ScrollBars.None;
            WordWrap = true;
            HideSelection = true;
        }

        public void AgregarLinea(string line)
        {
            AppendText(line + Environment.NewLine);
            SelectionStart = TextLength;
            ScrollToCaret();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEWHEEL)
            {
                int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                int lineas = SystemInformation.MouseWheelScrollLines;
                if (lineas <= 0)
                    lineas = 3;
                int direccion = delta > 0 ? -1 : 1;
                SendMessage(Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)(direccion * lineas));
                return;
            }
            base.WndProc(ref m);
        }

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }

    sealed class MainForm : Form
    {
        readonly TextBox txtDest = new TextBox();
        readonly LogBox txtLog = new LogBox();
        readonly Button btnDest = new Button();
        readonly Button btnUpdate = new Button();
        readonly Button btnCancel = new Button();
        readonly ProgressBar progress = new ProgressBar();
        readonly Label lblInfo = new Label();
        readonly Label lblStatus = new Label();
        readonly Label intro = new Label();
        readonly BackgroundWorker worker = new BackgroundWorker();
        Package package;
        string packagePath;
        volatile bool cancelFlag;
        string logPath;

        public MainForm()
        {
            Text = "Actualizador Forgotten Hope 2";
            Font = new Font("Segoe UI", 10f);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            SizeGripStyle = SizeGripStyle.Hide;
            ClientSize = new Size(520, 340);
            BackColor = Color.FromArgb(12, 12, 12);
            ForeColor = Color.White;
            try
            {
                using (Icon extraido = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location))
                {
                    if (extraido != null)
                        Icon = (Icon)extraido.Clone();
                }
            }
            catch (Exception)
            {
            }

            intro.Text = "Elige la carpeta mods\\fh2 del juego.";
            intro.ForeColor = Color.White;
            intro.SetBounds(12, 8, 496, 36);
            intro.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            var lblDest = new Label();
            lblDest.Text = "Carpeta del juego (mods\\fh2)";
            lblDest.ForeColor = Color.White;
            lblDest.SetBounds(12, 46, 380, 18);
            lblDest.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            txtDest.BackColor = Color.FromArgb(28, 28, 28);
            txtDest.ForeColor = Color.White;
            txtDest.BorderStyle = BorderStyle.FixedSingle;
            txtDest.Text = BuscarCarpetaFh2();
            txtDest.SetBounds(12, 66, 386, 24);
            txtDest.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            txtDest.TextChanged += delegate { ActualizarVersionDetectada(); };

            btnDest.Text = "Elegir";
            btnDest.SetBounds(406, 64, 102, 26);
            btnDest.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            StyleDarkButton(btnDest);
            btnDest.Click += delegate { PickFolder(); };

            lblInfo.ForeColor = Color.White;
            lblInfo.AutoEllipsis = true;
            lblInfo.SetBounds(12, 98, 496, 20);
            lblInfo.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            btnUpdate.Text = "Actualizar";
            btnUpdate.SetBounds(12, 126, 130, 32);
            btnUpdate.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            btnUpdate.BackColor = Color.FromArgb(20, 130, 55);
            btnUpdate.ForeColor = Color.White;
            btnUpdate.FlatStyle = FlatStyle.Flat;
            btnUpdate.FlatAppearance.BorderSize = 0;
            btnUpdate.Click += BtnUpdate_Click;

            btnCancel.Text = "Cancelar";
            btnCancel.SetBounds(150, 126, 110, 32);
            btnCancel.Enabled = false;
            StyleDarkButton(btnCancel);
            btnCancel.Click += delegate { cancelFlag = true; lblStatus.Text = "Se detendrá al terminar el archivo actual."; };

            progress.SetBounds(12, 168, 496, 16);
            progress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            lblStatus.Text = "En espera.";
            lblStatus.ForeColor = Color.FromArgb(220, 220, 220);
            lblStatus.SetBounds(12, 188, 496, 18);
            lblStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.None;
            txtLog.WordWrap = true;
            txtLog.Font = new Font("Consolas", 9f);
            txtLog.BackColor = Color.FromArgb(20, 20, 20);
            txtLog.ForeColor = Color.FromArgb(230, 230, 230);
            txtLog.BorderStyle = BorderStyle.FixedSingle;
            txtLog.SetBounds(12, 212, 496, 116);
            txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            Controls.Add(intro);
            Controls.Add(lblDest);
            Controls.Add(txtDest);
            Controls.Add(btnDest);
            Controls.Add(lblInfo);
            Controls.Add(btnUpdate);
            Controls.Add(btnCancel);
            Controls.Add(progress);
            Controls.Add(lblStatus);
            Controls.Add(txtLog);

            worker.DoWork += Worker_DoWork;
            worker.RunWorkerCompleted += Worker_Completed;
            Load += MainForm_Load;
        }

        // Barra de título oscura. El número 20 vale en Windows 10 reciente; el 19, en compilaciones anteriores.
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int useDark = 1;
                DwmSetWindowAttribute(Handle, 20, ref useDark, 4);
                DwmSetWindowAttribute(Handle, 19, ref useDark, 4);
            }
            catch (DllNotFoundException)
            {
            }
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

        static void StyleDarkButton(Button button)
        {
            button.BackColor = Color.FromArgb(42, 42, 42);
            button.ForeColor = Color.White;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
            button.FlatAppearance.BorderSize = 1;
        }

        // La ruta de siempre. Si ahi no esta el juego, se busca ForgottenHope2.exe
        // y la carpeta del mod queda al lado: mods\fh2.
        const string CarpetaPorDefecto = @"C:\Program Files (x86)\Forgotten Hope 2\mods\fh2";

        static string BuscarCarpetaFh2()
        {
            if (LooksLikeFh2(CarpetaPorDefecto))
                return CarpetaPorDefecto;

            string desdeRegistro = CarpetaDesdeRegistro();
            if (LooksLikeFh2(desdeRegistro))
                return desdeRegistro;

            foreach (DriveInfo unidad in DriveInfo.GetDrives())
            {
                try
                {
                    if (unidad.DriveType != DriveType.Fixed || !unidad.IsReady)
                        continue;
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                string raiz = unidad.RootDirectory.FullName;
                string[] ejecutables = new[]
                {
                    Path.Combine(raiz, @"Program Files (x86)\Forgotten Hope 2\ForgottenHope2.exe"),
                    Path.Combine(raiz, @"Program Files\Forgotten Hope 2\ForgottenHope2.exe"),
                    Path.Combine(raiz, @"Forgotten Hope 2\ForgottenHope2.exe"),
                    Path.Combine(raiz, @"Games\Forgotten Hope 2\ForgottenHope2.exe"),
                    Path.Combine(raiz, @"Juegos\Forgotten Hope 2\ForgottenHope2.exe")
                };
                foreach (string ejecutable in ejecutables)
                {
                    string carpeta = CarpetaDesdeEjecutable(ejecutable);
                    if (LooksLikeFh2(carpeta))
                        return carpeta;
                }
            }

            return CarpetaPorDefecto;
        }

        // InstallLocation o DisplayIcon del desinstalador suelen apuntar a la carpeta del juego.
        static string CarpetaDesdeRegistro()
        {
            try
            {
                return CarpetaDesdeRegistroInterno();
            }
            catch (System.Security.SecurityException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        static string CarpetaDesdeRegistroInterno()
        {
            string[] claves = new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            foreach (string rutaClave in claves)
            {
                using (RegistryKey clave = Registry.LocalMachine.OpenSubKey(rutaClave))
                {
                    if (clave == null)
                        continue;
                    foreach (string nombre in clave.GetSubKeyNames())
                    {
                        using (RegistryKey sub = clave.OpenSubKey(nombre))
                        {
                            if (sub == null)
                                continue;
                            string titulo = sub.GetValue("DisplayName") as string;
                            if (titulo == null || titulo.IndexOf("Forgotten Hope 2", StringComparison.OrdinalIgnoreCase) < 0)
                                continue;
                            string icono = sub.GetValue("DisplayIcon") as string;
                            string desdeIcono = CarpetaDesdeEjecutable(LimpiarRutaRegistro(icono));
                            if (LooksLikeFh2(desdeIcono))
                                return desdeIcono;
                            string instalacion = sub.GetValue("InstallLocation") as string;
                            string desdeInstalacion = CarpetaMod(LimpiarRutaRegistro(instalacion));
                            if (LooksLikeFh2(desdeInstalacion))
                                return desdeInstalacion;
                        }
                    }
                }
            }
            return null;
        }

        static string LimpiarRutaRegistro(string ruta)
        {
            if (string.IsNullOrWhiteSpace(ruta))
                return null;
            ruta = ruta.Trim().Trim('"');
            int coma = ruta.IndexOf(',');
            if (coma > 0)
                ruta = ruta.Substring(0, coma).Trim().Trim('"');
            return ruta;
        }

        // ForgottenHope2.exe esta en la raiz del juego. El mod esta en mods\fh2.
        static string CarpetaDesdeEjecutable(string rutaEjecutable)
        {
            if (string.IsNullOrWhiteSpace(rutaEjecutable) || !File.Exists(rutaEjecutable))
                return null;
            return Path.Combine(Path.GetDirectoryName(rutaEjecutable), "mods", "fh2");
        }

        static bool LooksLikeFh2(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
                return false;
            return File.Exists(Path.Combine(dir, "init.con")) && File.Exists(Path.Combine(dir, "mod.desc"));
        }

        // La version del mod esta en mods\fh2\mod.desc, dentro de <version>.
        static string LeerVersionMod(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
                return null;
            string path = Path.Combine(dir.Trim(), "mod.desc");
            try
            {
                if (!File.Exists(path))
                    return null;
                using (var reader = new StreamReader(path, Encoding.UTF8, true))
                {
                    char[] buffer = new char[8192];
                    int read = reader.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                        return null;
                    return ExtraerVersion(new string(buffer, 0, read));
                }
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        static string ExtraerVersion(string text)
        {
            const string open = "<version>";
            const string close = "</version>";
            int start = text.IndexOf(open, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return null;
            start += open.Length;
            int end = text.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
            if (end < 0 || end == start)
                return null;
            string value = text.Substring(start, end - start).Trim();
            if (value.Length == 0 || value.Length > 32 || value.IndexOf('<') >= 0)
                return null;
            return value;
        }

        void ActualizarVersionDetectada()
        {
            string version = LeerVersionMod(txtDest.Text);
            string texto = version == null ? "Versión actual: no detectada." : "Versión actual: " + version + ".";
            if (package != null && version != null && !string.Equals(version, package.FromVersion, StringComparison.OrdinalIgnoreCase))
                texto += " Se necesita la " + package.FromVersion + ".";
            if (package != null)
                texto += " Pesa " + FormatGb(package.CarriedBytes) + ".";
            lblInfo.Text = texto;
        }

        static string CarpetaMod(string ruta)
        {
            if (string.IsNullOrWhiteSpace(ruta))
                return null;
            ruta = ruta.TrimEnd('\\');
            if (ruta.EndsWith(@"\mods\fh2", StringComparison.OrdinalIgnoreCase))
                return ruta;
            return Path.Combine(ruta, "mods", "fh2");
        }

        void MainForm_Load(object sender, EventArgs e)
        {
            MinimumSize = Size;
            MaximumSize = Size;
            packagePath = Program.PackagePath();
            logPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "actualizador-fh2.log");
            string error;
            if (!Package.TryRead(packagePath, out package, out error))
            {
                btnUpdate.Enabled = false;
                lblStatus.Text = "No se pudo leer la actualización. " + error;
                ActualizarVersionDetectada();
                return;
            }
            Text = "Forgotten Hope 2  " + package.ToVersion;
            intro.Text = "De la " + package.FromVersion + " a la " + package.ToVersion
                + ". Elige mods\\fh2. Tiene que estar en la " + package.FromVersion + ".";
            ActualizarVersionDetectada();
        }

        void PickFolder()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Elige la carpeta mods\\fh2";
                if (Directory.Exists(txtDest.Text))
                    dialog.SelectedPath = txtDest.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    txtDest.Text = dialog.SelectedPath;
            }
        }

        void BtnUpdate_Click(object sender, EventArgs e)
        {
            string dest = txtDest.Text.Trim();
            string error = Validate(dest);
            if (error != null)
            {
                MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string versionDetectada = LeerVersionMod(dest);
            string message = "Se aplicará la actualización en:"
                + Environment.NewLine + Environment.NewLine + dest
                + Environment.NewLine + Environment.NewLine
                + "Versión actual: " + (versionDetectada ?? "no detectada") + "."
                + Environment.NewLine
                + "Pesa " + FormatGb(package.CarriedBytes) + ".";
            string root = Path.GetPathRoot(dest);
            if (!string.IsNullOrEmpty(root))
            {
                long free = new DriveInfo(root).AvailableFreeSpace;
                if (free < 2L * 1024L * 1024L * 1024L)
                    message += Environment.NewLine + Environment.NewLine + "Queda poco espacio libre en ese disco (" + FormatGb(free) + "). Al rearmar un zip hace falta espacio temporal.";
            }
            if (MessageBox.Show(this, message, "Confirmar actualización", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            cancelFlag = false;
            btnUpdate.Enabled = false;
            btnDest.Enabled = false;
            btnCancel.Enabled = true;
            txtLog.Clear();
            progress.Minimum = 0;
            progress.Maximum = Math.Max(1, package.Files.Count + package.DeleteFiles.Count);
            progress.Value = 0;
            try
            {
                File.WriteAllText(logPath, "Inicio " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
            }
            worker.RunWorkerAsync(dest);
        }

        void Worker_DoWork(object sender, DoWorkEventArgs e)
        {
            var patcher = new Patcher();
            patcher.CancelRequested = delegate { return cancelFlag; };
            patcher.OnLog = SafeLog;
            patcher.OnProgress = SafeProgress;
            e.Result = patcher.Apply(packagePath, package, (string)e.Argument);
        }

        void Worker_Completed(object sender, RunWorkerCompletedEventArgs e)
        {
            btnUpdate.Enabled = true;
            btnDest.Enabled = true;
            btnCancel.Enabled = false;
            if (e.Error != null)
            {
                lblStatus.Text = "Error.";
                MessageBox.Show(this, e.Error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            PatchResult result = (PatchResult)e.Result;
            string summary = "Actualizados: " + result.Copied
                + ". Borrados: " + result.Deleted
                + ". Errores: " + result.Errors + ".";
            if (result.Cancelled)
                summary = "Detenido. " + summary;
            lblStatus.Text = summary;
            SafeLog(summary);
            MessageBox.Show(this, summary, Text, MessageBoxButtons.OK, result.Errors == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        void SafeLog(string line)
        {
            if (IsDisposed)
                return;
            BeginInvoke((Action)delegate
            {
                txtLog.AgregarLinea(line);
                try
                {
                    File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch (IOException)
                {
                }
            });
        }

        void SafeProgress(int done, int total, string name)
        {
            if (IsDisposed)
                return;
            BeginInvoke((Action)delegate
            {
                progress.Maximum = Math.Max(1, total);
                progress.Value = Math.Min(progress.Maximum, Math.Max(0, done));
                lblStatus.Text = done + " / " + total + "  " + name;
            });
        }

        static string Validate(string dest)
        {
            if (dest.Length == 0)
                return "Elige la carpeta de destino.";
            if (!Directory.Exists(dest))
                return "La carpeta de destino no existe.";
            if (!LooksLikeFh2(dest))
                return "El destino no parece ser la carpeta mods\\fh2. Tiene que contener init.con y mod.desc.";
            string destFull = Path.GetFullPath(dest).TrimEnd('\\');
            if (destFull.IndexOf(" - copia", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Esa carpeta es una copia de seguridad. Elige la carpeta del juego que quieres actualizar.";
            return null;
        }

        static string FormatGb(long bytes)
        {
            return (bytes / 1024d / 1024d / 1024d).ToString("0.00") + " GB";
        }
    }
}
