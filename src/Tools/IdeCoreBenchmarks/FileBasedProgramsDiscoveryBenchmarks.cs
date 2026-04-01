// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#if NET

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using BenchmarkDotNet.Attributes;

namespace IdeCoreBenchmarks;

[MemoryDiagnoser]
public class FileBasedProgramsDiscoveryBenchmarks
{
    private static readonly StringComparer s_pathComparer = StringComparer.OrdinalIgnoreCase;

    private static readonly HashSet<string> s_ignoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "artifacts",
        "bin",
        "obj",
        "node_modules"
    };

    // Set this to the workspace folder you want to benchmark against.
    // The roslyn repo root is a good default since it has many .cs files and csproj directories.
    [Params(@"C:\Users\dabarbet\source\repos\roslyn")]
    public string WorkspaceFolder { get; set; } = null!;

    private string _cacheDirectory = null!;
    private string _cacheFilePath = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _cacheDirectory = GetDiscoveryCacheDirectory(WorkspaceFolder);
        _cacheFilePath = Path.Join(_cacheDirectory, "cache.json");

        // Warm up the cache by running the cached approach once.
        _ = FindEntryPointsCached().ToList();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        try
        {
            if (Directory.Exists(_cacheDirectory))
                Directory.Delete(_cacheDirectory, recursive: true);
        }
        catch { }
    }

    [Benchmark(Description = "Cached (incremental walk)")]
    public List<string> Cached()
    {
        return FindEntryPointsCached().ToList();
    }

    // [Benchmark(Description = "Cache files only (walk dirs)")]
    // public List<string> CachedFilesOnly()
    // {
    //     return FindEntryPointsCachedFilesOnly().ToList();
    // }

    // [Benchmark(Description = "Cache dirs only (read files)")]
    // public List<string> CachedDirsOnly()
    // {
    //     return FindEntryPointsCachedDirsOnly().ToList();
    // }

    [Benchmark(Description = "Parallel full walk (no cache)")]
    public List<string> ParallelFullWalk()
    {
        return FindEntryPointsParallelFullWalk();
    }

    // [Benchmark(Baseline = true, Description = "Full walk (no cache)")]
    // public List<string> FullWalk()
    // {
    //     return FindEntryPointsFullWalk().ToList();
    // }

    // ========================
    // Cached approach (mirrors the production code)
    // ========================

    private IEnumerable<string> FindEntryPointsCached()
    {
        Cache? cache = null;
        try
        {
            using var cacheFile = File.OpenRead(_cacheFilePath);
            cache = JsonSerializer.Deserialize<Cache>(cacheFile);

            if (cache?.WorkspacePath.Equals(WorkspaceFolder, StringComparison.OrdinalIgnoreCase) == false
                || cache is { FileBasedAppFullPaths.IsDefault: true } or { DirectoriesContainingCsproj.IsDefault: true })
            {
                cache = null;
            }
        }
        catch
        {
        }

        cache ??= new Cache(WorkspaceFolder, DateTimeOffset.MinValue, ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);

        var walkStartTimeUtc = GetWalkStartTimeUtc(cache);

        var newFileBasedAppsBuilder = new List<string>(cache.FileBasedAppFullPaths.Length);

        // Initial cache loop: validate known file-based apps
        foreach (var fileBasedAppPath in cache.FileBasedAppFullPaths)
        {
            if (!File.Exists(fileBasedAppPath))
                continue;

            if (IsContainedInCsprojCone(fileBasedAppPath, WorkspaceFolder))
                continue;

            if (!IsFileBasedApp(fileBasedAppPath))
                continue;

            newFileBasedAppsBuilder.Add(fileBasedAppPath);
            yield return fileBasedAppPath;
        }

        // Incremental walk for changes since last walk
        var directoriesContainingCsprojBuilder = new List<string>(cache.DirectoriesContainingCsproj.Length);
        if (!Directory.EnumerateFiles(cache.WorkspacePath, "*.csproj").Any())
        {
            var enumerator = new IncrementalEntryPointEnumerator(cache, directoriesContainingCsprojBuilder);
            while (enumerator.MoveNext())
            {
                var fileBasedAppPath = enumerator.Current;
                newFileBasedAppsBuilder.Add(fileBasedAppPath);
                yield return fileBasedAppPath;
            }

            newFileBasedAppsBuilder.Sort(s_pathComparer);
            directoriesContainingCsprojBuilder.Sort(s_pathComparer);
        }

        var newCache = new Cache(WorkspaceFolder, walkStartTimeUtc, newFileBasedAppsBuilder.ToImmutableArray(), directoriesContainingCsprojBuilder.ToImmutableArray());
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFilePath)!);
            using var file = File.Create(_cacheFilePath);
            JsonSerializer.Serialize(file, newCache);
        }
        catch
        {
        }
    }

    private DateTimeOffset GetWalkStartTimeUtc(Cache cache)
    {
        try
        {
            var sentinelPath = Path.Join(_cacheDirectory, $".walk-timestamp-{Guid.NewGuid()}");
            File.WriteAllBytes(sentinelPath, Array.Empty<byte>());
            var lastWriteTime = File.GetLastWriteTimeUtc(sentinelPath);
            File.Delete(sentinelPath);
            return lastWriteTime;
        }
        catch
        {
            return cache.LastWalkTimeUtc;
        }
    }

    // ========================
    // Cache files only approach (cache FBP decisions, walk dirs for csproj)
    // ========================

    private IEnumerable<string> FindEntryPointsCachedFilesOnly()
    {
        var cache = LoadCache();
        var cachedFbpPaths = cache != null
            ? new HashSet<string>(cache.FileBasedAppFullPaths, s_pathComparer)
            : new HashSet<string>(s_pathComparer);

        if (Directory.EnumerateFiles(WorkspaceFolder, "*.csproj").Any())
            yield break;

        var enumerator = new CachedFilesOnlyEnumerator(WorkspaceFolder, cachedFbpPaths);
        while (enumerator.MoveNext())
            yield return enumerator.Current;
    }

    // ========================
    // Cache dirs only approach (cache csproj dirs, read files every time)
    // ========================

    private IEnumerable<string> FindEntryPointsCachedDirsOnly()
    {
        var cache = LoadCache();
        var cachedCsprojDirs = cache != null
            ? new HashSet<string>(cache.DirectoriesContainingCsproj, s_pathComparer)
            : new HashSet<string>(s_pathComparer);

        if (Directory.EnumerateFiles(WorkspaceFolder, "*.csproj").Any())
            yield break;

        var enumerator = new CachedDirsOnlyEnumerator(WorkspaceFolder, cachedCsprojDirs);
        while (enumerator.MoveNext())
            yield return enumerator.Current;
    }

    private Cache? LoadCache()
    {
        try
        {
            using var cacheFile = File.OpenRead(_cacheFilePath);
            var cache = JsonSerializer.Deserialize<Cache>(cacheFile);

            if (cache?.WorkspacePath.Equals(WorkspaceFolder, StringComparison.OrdinalIgnoreCase) == false
                || cache is { FileBasedAppFullPaths.IsDefault: true } or { DirectoriesContainingCsproj.IsDefault: true })
            {
                return null;
            }

            return cache;
        }
        catch
        {
            return null;
        }
    }

    // ========================
    // Parallel full walk approach (no cache, parallel sibling dirs + file checks)
    // ========================

    private List<string> FindEntryPointsParallelFullWalk()
    {
        if (Directory.EnumerateFiles(WorkspaceFolder, "*.csproj").Any())
            return new List<string>();

        var results = new ConcurrentQueue<string>();
        WalkDirectoryParallel(WorkspaceFolder, results);
        return results.ToList();
    }

    private static void WalkDirectoryParallel(string directory, ConcurrentQueue<string> results)
    {
        // Collect subdirectories into a rented array to avoid List<string> allocation
        var rented = ArrayPool<string>.Shared.Rent(16);
        var subdirCount = 0;

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (Directory.Exists(entry))
                {
                    var dirName = Path.GetFileName(entry);
                    if (!s_ignoredDirectories.Contains(dirName)
                        && !Directory.EnumerateFiles(entry, "*.csproj").Any())
                    {
                        if (subdirCount == rented.Length)
                        {
                            var larger = ArrayPool<string>.Shared.Rent(rented.Length * 2);
                            rented.AsSpan(0, subdirCount).CopyTo(larger);
                            ArrayPool<string>.Shared.Return(rented, clearArray: true);
                            rented = larger;
                        }

                        rented[subdirCount++] = entry;
                    }
                }
                else if (Path.GetExtension(entry).Equals(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    if (IsFileBasedApp(entry))
                        results.Enqueue(entry);
                }
            }
        }
        catch
        {
            ArrayPool<string>.Shared.Return(rented, clearArray: true);
            return;
        }

        // Recurse into sibling subdirectories in parallel
        var captured = rented;
        Parallel.For(0, subdirCount, i =>
        {
            WalkDirectoryParallel(captured[i], results);
        });

        ArrayPool<string>.Shared.Return(rented, clearArray: true);
    }

    // ========================
    // Full walk approach (no cache, just enumerate everything)
    // ========================

    private IEnumerable<string> FindEntryPointsFullWalk()
    {
        if (Directory.EnumerateFiles(WorkspaceFolder, "*.csproj").Any())
            yield break;

        var enumerator = new FullWalkEnumerator(WorkspaceFolder);
        while (enumerator.MoveNext())
            yield return enumerator.Current;
    }

    private sealed class FullWalkEnumerator : FileSystemEnumerator<string>
    {
        public FullWalkEnumerator(string workspaceFolder)
            : base(workspaceFolder, new EnumerationOptions { RecurseSubdirectories = true })
        {
        }
        protected override string TransformEntry(ref FileSystemEntry entry) => entry.ToFullPath();

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            if (entry.IsDirectory || !Path.GetExtension(entry.FileName).Equals(".cs", StringComparison.OrdinalIgnoreCase))
                return false;

            return IsFileBasedApp(entry.ToFullPath());
        }

        protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry)
        {
            if (s_ignoredDirectories.Contains(entry.FileName.ToString()))
                return false;

            return !Directory.EnumerateFiles(entry.ToFullPath(), "*.csproj").Any();
        }
    }

    // ========================
    // Shared helpers
    // ========================

    private static bool IsFileBasedApp(string fullPath)
    {
        try
        {
            using var fileStream = File.OpenRead(fullPath);
            var toRead = (int)Math.Min(5, fileStream.Length);
            Span<byte> bytes = stackalloc byte[5];
            fileStream.ReadExactly(bytes[..toRead]);
            return bytes is [(byte)'#', (byte)'!', ..] or [0xEF, 0xBB, 0xBF, (byte)'#', (byte)'!'];
        }
        catch
        {
            return false;
        }
    }

    private static bool IsContainedInCsprojCone(string csFilePath, string workspaceFolder)
    {
        var directoryName = Path.GetDirectoryName(csFilePath);
        while (directoryName != null
            && directoryName.StartsWith(workspaceFolder, StringComparison.OrdinalIgnoreCase)
            && directoryName.Length >= workspaceFolder.Length)
        {
            if (Directory.EnumerateFiles(directoryName, "*.csproj").Any())
                return true;

            directoryName = Path.GetDirectoryName(directoryName);
        }

        return false;
    }

    // ========================
    // Cache files only enumerator
    // Walks all directories for csproj, but uses cached FBP decisions to skip file reads.
    // ========================

    private sealed class CachedFilesOnlyEnumerator : FileSystemEnumerator<string>
    {
        private readonly HashSet<string> _cachedFbpPaths;

        public CachedFilesOnlyEnumerator(string workspaceFolder, HashSet<string> cachedFbpPaths)
            : base(workspaceFolder, new EnumerationOptions { RecurseSubdirectories = true })
        {
            _cachedFbpPaths = cachedFbpPaths;
        }

        protected override string TransformEntry(ref FileSystemEntry entry) => entry.ToFullPath();

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            if (entry.IsDirectory || !Path.GetExtension(entry.FileName).Equals(".cs", StringComparison.OrdinalIgnoreCase))
                return false;

            var fullPath = entry.ToFullPath();
            // Use cached FBP decision if available, otherwise read the file
            return _cachedFbpPaths.Contains(fullPath) || IsFileBasedApp(fullPath);
        }

        protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry)
        {
            if (s_ignoredDirectories.Contains(entry.FileName.ToString()))
                return false;

            // Always walk directories for csproj (no directory cache)
            return !Directory.EnumerateFiles(entry.ToFullPath(), "*.csproj").Any();
        }
    }

    // ========================
    // Cache dirs only enumerator
    // Uses cached csproj dirs to skip subtrees, but reads every .cs file.
    // ========================

    private sealed class CachedDirsOnlyEnumerator : FileSystemEnumerator<string>
    {
        private readonly HashSet<string> _cachedCsprojDirs;

        public CachedDirsOnlyEnumerator(string workspaceFolder, HashSet<string> cachedCsprojDirs)
            : base(workspaceFolder, new EnumerationOptions { RecurseSubdirectories = true })
        {
            _cachedCsprojDirs = cachedCsprojDirs;
        }

        protected override string TransformEntry(ref FileSystemEntry entry) => entry.ToFullPath();

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            if (entry.IsDirectory || !Path.GetExtension(entry.FileName).Equals(".cs", StringComparison.OrdinalIgnoreCase))
                return false;

            // Always read the file to check (no file content cache)
            return IsFileBasedApp(entry.ToFullPath());
        }

        protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry)
        {
            if (s_ignoredDirectories.Contains(entry.FileName.ToString()))
                return false;

            // Use cached csproj dirs to skip subtrees without enumeration
            return !_cachedCsprojDirs.Contains(entry.ToFullPath());
        }
    }

    // ========================
    // Incremental enumerator (mirrors production code)
    // ========================

    private class IncrementalEntryPointEnumerator : FileSystemEnumerator<string>
    {
        private readonly Cache _cache;
        private readonly List<string> _directoriesContainingCsprojBuilder;
        private readonly HashSet<string> _newerDirectories = new(s_pathComparer);

        public IncrementalEntryPointEnumerator(Cache cache, List<string> directoriesContainingCsprojBuilder)
            : base(cache.WorkspacePath, new EnumerationOptions { RecurseSubdirectories = true })
        {
            _cache = cache;
            _directoriesContainingCsprojBuilder = directoriesContainingCsprojBuilder;

            var workspaceDirectoryInfo = new DirectoryInfo(_cache.WorkspacePath);
            if (workspaceDirectoryInfo.CreationTimeUtc >= cache.LastWalkTimeUtc
                || workspaceDirectoryInfo.LastWriteTimeUtc >= cache.LastWalkTimeUtc)
            {
                _newerDirectories.Add(workspaceDirectoryInfo.FullName);
            }
        }

        protected override string TransformEntry(ref FileSystemEntry entry) => entry.ToFullPath();

        private bool IsCacheUpToDate(ref FileSystemEntry entry)
        {
            if (_newerDirectories.Contains(entry.Directory.ToString()))
                return false;

            if (entry.CreationTimeUtc >= _cache.LastWalkTimeUtc
                || entry.LastWriteTimeUtc >= _cache.LastWalkTimeUtc)
            {
                return false;
            }

            if (entry.IsDirectory)
            {
                var directoryInfo = new DirectoryInfo(entry.ToFullPath());
                if (directoryInfo.CreationTimeUtc >= _cache.LastWalkTimeUtc
                    || directoryInfo.LastWriteTimeUtc >= _cache.LastWalkTimeUtc)
                {
                    return false;
                }
            }

            return true;
        }

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            if (entry.IsDirectory || !Path.GetExtension(entry.FileName).Equals(".cs", StringComparison.OrdinalIgnoreCase))
                return false;

            if (IsCacheUpToDate(ref entry))
                return false;

            var fullPath = entry.ToFullPath();
            if (_cache.FileBasedAppFullPaths.BinarySearch(fullPath, s_pathComparer) >= 0)
                return false;

            return IsFileBasedApp(fullPath);
        }

        protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry)
        {
            if (s_ignoredDirectories.Contains(entry.FileName.ToString()))
                return false;

            var fullPath = entry.ToFullPath();
            if (IsCacheUpToDate(ref entry))
            {
                if (_cache.DirectoriesContainingCsproj.BinarySearch(fullPath, s_pathComparer) >= 0)
                {
                    _directoriesContainingCsprojBuilder.Add(fullPath);
                    return false;
                }

                return true;
            }

            if (Directory.EnumerateFiles(fullPath, "*.csproj").Any())
            {
                _directoriesContainingCsprojBuilder.Add(fullPath);
                return false;
            }

            _newerDirectories.Add(fullPath);
            return true;
        }
    }

    // ========================
    // Cache types
    // ========================

    internal sealed record Cache
    {
        public string WorkspacePath { get; init; } = "";
        public DateTimeOffset LastWalkTimeUtc { get; init; }
        public ImmutableArray<string> FileBasedAppFullPaths { get; init; }
        public ImmutableArray<string> DirectoriesContainingCsproj { get; init; }

        public Cache() { }

        public Cache(string workspacePath, DateTimeOffset lastWalkTimeUtc, ImmutableArray<string> fileBasedAppFullPaths, ImmutableArray<string> directoriesContainingCsproj)
        {
            WorkspacePath = workspacePath;
            LastWalkTimeUtc = lastWalkTimeUtc;
            FileBasedAppFullPaths = fileBasedAppFullPaths;
            DirectoriesContainingCsproj = directoriesContainingCsproj;
        }
    }

    private static string GetDiscoveryCacheDirectory(string workspaceFolder)
    {
        var hash = HashWithNormalizedCasing(workspaceFolder);
        var fileName = Path.GetFileNameWithoutExtension(workspaceFolder);
        var directoryName = $"{fileName}-{hash}";
        var tempDirectory = Path.GetTempPath();
        return Path.Join(tempDirectory, "dotnet", "runfile-discovery-benchmark", directoryName);
    }

    private static string HashWithNormalizedCasing(string path)
    {
        var normalized = path.ToUpperInvariant();
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(hashBytes);
    }
}

#endif
