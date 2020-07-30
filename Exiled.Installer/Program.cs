// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Exiled Team">
// Copyright (c) Exiled Team. All rights reserved.
// Licensed under the CC BY-SA 3.0 license.
// </copyright>
// -----------------------------------------------------------------------

namespace Exiled.Installer
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Reflection;
    using System.Threading.Tasks;

    using ICSharpCode.SharpZipLib.GZip;
    using ICSharpCode.SharpZipLib.Tar;

    using Octokit;

    /// <summary>
    /// Installs Exiled libraries on a SCP:SL game server.
    /// </summary>
    internal static class Program
    {
#pragma warning disable SA1310 // Field names should not contain underscore
        private const long REPO_ID = 231269519;
        private const string EXILED_ASSET_NAME = "exiled.tar.gz";
        private const string EXILED_FOLDER_NAME = "EXILED";
        private const string DEPENDENCY_FOLDER = "dependencies";
        private const string TARGET_FILE_NAME = "Assembly-CSharp.dll";
#pragma warning restore SA1310 // Field names should not contain underscore

        private static readonly string ExiledTargetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), EXILED_FOLDER_NAME);
        private static readonly string[] TargetSubfolders = { "SCPSL_Data", "Managed" };

        /// <summary>
        /// Entry point of the program.
        /// </summary>
        /// <param name="args">Extra arguments, not required.</param>
        /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
        public static async Task Main(string[] args)
        {
            await MainSafe(args).ConfigureAwait(false);
        }

        private static async Task MainSafe(string[] args)
        {
            try
            {
                if (args.Length == 0)
                    args = new[] { "--pre-release" };

                if (!ProcessTargetFilePath(args.FirstOrDefault(a => !a.Contains("--pre-release", StringComparison.OrdinalIgnoreCase)), out var targetFilePath))
                {
                    Console.WriteLine(string.Join(Environment.NewLine, args));
                    throw new FileNotFoundException("Requires an argument with the path to Assembly/game");
                }

                EnsureDirExists(ExiledTargetPath);

                Console.WriteLine("Getting latest download URL...");
                var includePrerelease = args.Any(a => a.Contains("--pre-release", StringComparison.OrdinalIgnoreCase));
                if (includePrerelease)
                    Console.WriteLine("Including pre-releases");

                var client = new GitHubClient(new ProductHeaderValue(Assembly.GetExecutingAssembly().GetName().Name));
                var releases = (await client.Repository.Release.GetAll(REPO_ID).ConfigureAwait(false)).OrderByDescending(r => r.CreatedAt.Ticks);
                var latestRelease = releases.FirstOrDefault(r => (r.Prerelease && includePrerelease) || !r.Prerelease);
                if (latestRelease is null)
                {
                    Console.Write("RELEASES:");
                    Console.CursorLeft++;
                    Console.WriteLine(string.Join(Environment.NewLine, releases.Select(FormatRelease)));
                    throw new InvalidOperationException("Couldn't find release");
                }

                Console.WriteLine("Release found!");
                Console.WriteLine(FormatRelease(latestRelease));

                var exiledAsset = latestRelease.Assets.FirstOrDefault(a => a.Name.Equals(EXILED_ASSET_NAME, StringComparison.OrdinalIgnoreCase));
                if (exiledAsset is null)
                {
                    Console.Write("ASSETS:");
                    Console.CursorLeft++;
                    Console.WriteLine(string.Join(Environment.NewLine, latestRelease.Assets.Select(FormatAsset)));
                    throw new InvalidOperationException("Couldn't find asset");
                }

                Console.WriteLine("Asset found!");
                Console.WriteLine(FormatAsset(exiledAsset));

                var downloadUrl = exiledAsset.BrowserDownloadUrl;
                using var httpClient = new HttpClient();
                var downloadResult = await httpClient.GetAsync(downloadUrl).ConfigureAwait(false);
                var downloadArchiveStream = await downloadResult.Content.ReadAsStreamAsync().ConfigureAwait(false);

                using var gzInputStream = new GZipInputStream(downloadArchiveStream);
                using var tarInputStream = new TarInputStream(gzInputStream);

                TarEntry entry;
                while (!((entry = tarInputStream.GetNextEntry()) is null))
                    ProcessTarEntry(targetFilePath, tarInputStream, entry);

                Console.WriteLine("Installation complete.");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                Console.WriteLine("TAKE A SCREENSOT AND SEND TO THE #SUPPORT CHANNEL!1");
                Console.Read();
            }
        }

        private static string FormatRelease(Release r)
        {
            return $"PR: {r.Prerelease} | ID: {r.Id} | TAG: {r.TagName}";
        }

        private static string FormatAsset(ReleaseAsset a)
        {
            return $"ID: {a.Id} | NAME: {a.Name} | URL: {a.Url}";
        }

        private static bool AllowExtract(string fileName, out string path)
        {
            path = Path.GetFullPath(Path.Combine(ExiledTargetPath, fileName));
            return File.Exists(path);
        }

        private static void ProcessTarEntry(string targetFilePath, TarInputStream tarInputStream, TarEntry entry)
        {
            if (entry.IsDirectory)
            {
                var entries = entry.GetDirectoryEntries();
                for (int z = 0; z < entries.Length; z++)
                {
                    ProcessTarEntry(targetFilePath, tarInputStream, entries[z]);
                }
            }
            else
            {
                Console.WriteLine($"Processing: {entry.Name}");
                if (entry.Name.StartsWith(EXILED_FOLDER_NAME, StringComparison.OrdinalIgnoreCase))
                {
                    // Remeber about the separator char
                    var fileName = entry.Name.Substring(EXILED_FOLDER_NAME.Length + 1);
                    var allowExtract = AllowExtract(fileName, out var path);
                    if (allowExtract || entry.Name.Contains(DEPENDENCY_FOLDER, StringComparison.OrdinalIgnoreCase))
                        ExtractEntry(tarInputStream, entry, path);
                }
                else if (entry.Name.Equals(TARGET_FILE_NAME))
                {
                    ExtractEntry(tarInputStream, entry, targetFilePath);
                }
            }
        }

        private static void ExtractEntry(TarInputStream tarInputStream, TarEntry entry, string path)
        {
            Console.WriteLine($"Extracting '{Path.GetFileName(entry.Name.Replace('/', Path.DirectorySeparatorChar))}' into '{path}'...");

            EnsureDirExists(Path.GetPathRoot(path) !);

            FileStream? fs = null;
            try
            {
                fs = new FileStream(path, System.IO.FileMode.Create, FileAccess.Write, FileShare.None);
                tarInputStream.CopyEntryContents(fs);
            }
            catch (Exception ex)
            {
                Console.WriteLine("An exception occurred while trying to extract a file");
                Console.WriteLine(ex);
            }
            finally
            {
                fs?.Dispose();
            }
        }

        private static bool ProcessTargetFilePath(string argTargetPath, out string path)
        {
            var linkedSubfolders = string.Join(Path.DirectorySeparatorChar, TargetSubfolders);

            // can be null if couldn't be found
            if (string.IsNullOrEmpty(argTargetPath))
            {
                var combined = Path.Combine(argTargetPath, linkedSubfolders, TARGET_FILE_NAME);
                if (File.Exists(combined))
                {
                    path = combined;
                    return true;
                }
            }

            var curDir = Directory.GetCurrentDirectory();
            var combined2 = Path.Combine(curDir, linkedSubfolders, TARGET_FILE_NAME);
            if (File.Exists(combined2))
            {
                path = combined2;
                return true;
            }

            path = string.Empty;
            return false;
        }

        private static void EnsureDirExists(string pathToDir)
        {
            if (!Directory.Exists(pathToDir))
                Directory.CreateDirectory(pathToDir);
        }
    }
}
