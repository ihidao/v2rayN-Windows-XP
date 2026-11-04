using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using ICSharpCode.SharpZipLib.Zip;

namespace v2rayN.Tool
{
    public static class FileManager
    {
        public static bool ByteArrayToFile(string fileName, byte[] content)
        {
            try
            {
                using (FileStream fs = new FileStream(fileName, FileMode.Create, FileAccess.Write))
                    fs.Write(content, 0, content.Length);
                return true;
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
            }
            return false;
        }

        public static void UncompressFile(string fileName, byte[] content)
        {
            try
            {
                byte[] buffer = new byte[4096];
                int n;
                using (FileStream fs = File.Create(fileName))
                using (GZipStream input = new GZipStream(new MemoryStream(content), CompressionMode.Decompress, false))
                {
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                        fs.Write(buffer, 0, n);
                }
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
            }
        }

        public static string NonExclusiveReadAllText(string path)
        {
            return NonExclusiveReadAllText(path, Encoding.Default);
        }

        public static string NonExclusiveReadAllText(string path, Encoding encoding)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader sr = new StreamReader(fs, encoding))
                    return sr.ReadToEnd();
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
                throw;
            }
        }

        public static bool ZipExtractToFile(string fileName, string ignoredName)
        {
            try
            {
                using (ZipFile archive = new ZipFile(fileName))
                {
                    foreach (ZipEntry entry in archive)
                    {
                        if (entry.IsDirectory || entry.Size == 0)
                            continue;
                        if (!Utils.IsNullOrEmpty(ignoredName) && entry.Name.Contains(ignoredName))
                            continue;

                        string outputPath = Utils.GetPath(Path.GetFileName(entry.Name));
                        if (!IsSafeOutputPath(Utils.StartupPath(), outputPath))
                            continue;
                        ExtractEntry(archive, entry, outputPath);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
                return false;
            }
        }

        public static bool ZipExtractToFullFile(string fileName)
        {
            try
            {
                using (ZipFile archive = new ZipFile(fileName))
                {
                    foreach (ZipEntry entry in archive)
                    {
                        if (entry.IsDirectory || entry.Size == 0)
                            continue;

                        string outputPath = Utils.GetPath(entry.Name.Replace('/', Path.DirectorySeparatorChar));
                        if (!IsSafeOutputPath(Utils.StartupPath(), outputPath))
                            throw new IOException("Invalid ZIP entry path: " + entry.Name);

                        string directory = Path.GetDirectoryName(outputPath);
                        if (!Directory.Exists(directory))
                            Directory.CreateDirectory(directory);
                        ExtractEntry(archive, entry, outputPath);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Utils.SaveLog(ex.Message, ex);
                return false;
            }
        }

        private static void ExtractEntry(ZipFile archive, ZipEntry entry, string outputPath)
        {
            string directory = Path.GetDirectoryName(outputPath);
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            using (Stream input = archive.GetInputStream(entry))
            using (FileStream output = File.Create(outputPath))
            {
                byte[] buffer = new byte[8192];
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                    output.Write(buffer, 0, count);
            }
        }

        private static bool IsSafeOutputPath(string root, string path)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
    }
}