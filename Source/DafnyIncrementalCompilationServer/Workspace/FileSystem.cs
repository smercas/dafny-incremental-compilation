using DafnyCore.Options;
using Microsoft.Dafny.LanguageServer.Workspace;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Microsoft.Extensions.Logging;
using System;
using System.Collections;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Dafny.IncrementalCompilation.Workspace {
  public class FileSystem : IFileSystem {
    abstract class BaseFileSystem : IFileSystem {
      public ILogger<FileSystem> Logger { get; }
      public IReadOnlyCollection<Uri> URIs { get; }
      public string URIsAsString { get; }

      public BaseFileSystem(ILogger<FileSystem> logger, IReadOnlyCollection<Uri> uris) {
        this.Logger = logger;
        this.URIs = uris;
        URIsAsString = $"[{string.Join(", ", URIs)}]";
      }

      public bool Exists(Uri path) {
        if (URIs.Contains(path)) { return true; }
        Logger.LogWarning("couldn't find {} in existing files ({}), will check the disk", path, URIsAsString);
        return OnDiskFileSystem.Instance.Exists(path);
      }
      virtual public DirectoryInfoBase GetDirectoryInfoBase(string root) {
        var inMemoryFiles = URIs.Select(fileUri => fileUri.LocalPath);
        var inMemory = new InMemoryDirectoryInfoFromDotNet8(root, inMemoryFiles);
        return new CombinedDirectoryInfo([inMemory, OnDiskFileSystem.Instance.GetDirectoryInfoBase(root)]);
      }
      virtual public FileSnapshot ReadFile(Uri uri) {
        Logger.LogWarning("couldn't find {} in existing files ({}), will read form disk", uri, URIsAsString);
        return OnDiskFileSystem.Instance.ReadFile(uri);
      }
      virtual public void ApplyModification(IncCompModifications modification) { throw new InvalidOperationException(); }
    }
    class InitialFileSystem(ILogger<FileSystem> logger, IReadOnlyCollection<Uri> files) : BaseFileSystem(logger, files) {
      private FrozenDictionary<Uri, string> Files { get; } = files.Select(uri => KeyValuePair.Create(uri, File.ReadAllText(uri.LocalPath))).ToFrozenDictionary();
      public override FileSnapshot ReadFile(Uri uri) {
        if (Files.TryGetValue(uri, out var contents)) {
          return new FileSnapshot(new StringReader(contents), null);
        }
        return base.ReadFile(uri);
      }
    }
    class SubsequentFileSystem : BaseFileSystem {
      private FrozenDictionary<Uri, (
        FrozenSet<int> EntryPoints,
        FrozenDictionary<int, ImmutableList<(int EntryPoint, Token Token)>> EntryPointsAndPositionsByLine,
        Maybe<FrozenDictionary<int, string>> Modifications,
        ImmutableList<string> UnmodifiedContents,
        Maybe<FileSnapshot> Cache
      )> Info { get; }
      private class Maybe<T> where T : class {
        public Maybe() { value = null; }
        public Maybe(T value) { this.value = value; }
        private T? value;
        public T Value { get => value!; set => this.value = value; }
        public bool HasValue => value is not null;
      }
      public SubsequentFileSystem(InitialFileSystem ifs, IReadOnlyDictionary<int, Token> positionsByEntryPoints) : base(ifs.Logger, ifs.URIs) {
        Info = ifs.URIs.ToFrozenDictionary(
          uri => uri, uri => {
            var entryPoints = positionsByEntryPoints.SelectWhere(p => (p.Value.Uri == uri, p.Key)).ToFrozenSet();
            return (
              entryPoints,
              positionsByEntryPoints.Where(p => p.Value.Uri == uri).GroupBy(p => p.Value.line - 1).ToFrozenDictionary(
                gg => gg.Key,
                gg => gg.OrderByDescending(p => p.Value.col).Select(p => (p.Key, p.Value)).ToImmutableList()
              ),
              new Maybe<FrozenDictionary<int, string>>(),
              File.ReadAllLines(uri.LocalPath).ToImmutableList(),
              new Maybe<FileSnapshot>()
            );
          }
        );
      }
      public override FileSnapshot ReadFile(Uri uri) {
        Contract.Requires(!Info.ContainsKey(uri) || Info[uri].Modifications.HasValue);
        if (!Info.TryGetValue(uri, out var info)) { return base.ReadFile(uri); }
        FileSnapshot ProcessBeforeReturn(string s) {
          info.Cache.Value = new FileSnapshot(new StringReader(s), null);
          return info.Cache.Value;
        }
        // previously computed
        if (info.Cache.HasValue) { return info.Cache.Value; }
        // no modifications
        if (info.Modifications is { HasValue: false } or { HasValue: true, Value.Count: 0 }) {
          return ProcessBeforeReturn(string.Join(null, info.UnmodifiedContents));
        }
        var overwritesByLine = new Dictionary<int, string>();
        foreach (var (line, entryPointsAndTokens) in info.EntryPointsAndPositionsByLine) {
          foreach (var (entryPoint, token, modification) in entryPointsAndTokens.SelectWhere(p => (info.Modifications.Value.TryGetValue(p.EntryPoint, out var m), (p.EntryPoint, p.Token, m!)))) {
            overwritesByLine.TryAdd(line, info.UnmodifiedContents[line]);
            overwritesByLine[line] = overwritesByLine[line].Insert(token.col - 1, modification);
          }
        }
        var builder = new StringBuilder();
        foreach (var (line, idx) in info.UnmodifiedContents.Indexed()) {
          if (overwritesByLine.TryGetValue(idx, out var newline)) {
            builder.Append(newline);
          } else {
            builder.Append(line);
          }
        }
        return ProcessBeforeReturn(builder.ToString());
      }
      public void ApplyModifications(IncCompModifications modifications) {
        foreach (var cache in Info.Values.Select(v => v.Cache)) { cache.Value = null!; }
        foreach (var uri in Info.Keys) {
          if (false) {
            // take modification from `modifications[uri]`
          } else {
            Info[uri].Modifications.Value = null!;
          }
        }
      }
    }
    private class Entry(TextBuffer buffer, int? version) {
      public TextBuffer Buffer { get; set; } = buffer;
      public int? Version { get; set; } = version; // TODO: see if only increments are necessary in this context
    }
    private readonly ILogger<FileSystem> logger;
    private readonly FrozenDictionary<Uri, Entry> originalFiles;

    public FileSystem(ILogger<FileSystem> logger, IReadOnlyCollection<Uri> files) {
      Contract.Assert(Contract.ForAll(files, file => File.Exists(file.LocalPath)));
      this.logger = logger;
      this.originalFiles = files.Select(uri => KeyValuePair.Create(uri, new Entry(new(File.ReadAllText(uri.LocalPath)), null))).ToFrozenDictionary();
    }

    public void ApplyModification(IncCompModifications modification) {
      //IPMTODO: this and the changes system at large
    }

    public FileSnapshot ReadFile(Uri uri) {
      if (originalFiles.TryGetValue(uri, out var entry)) {
        return new FileSnapshot(new StringReader(entry.Buffer.Text), entry.Version);
      }

      return OnDiskFileSystem.Instance.ReadFile(uri);
    }
    public bool Exists(Uri path) => originalFiles.ContainsKey(path) || OnDiskFileSystem.Instance.Exists(path);
    public DirectoryInfoBase GetDirectoryInfoBase(string root) {
      var inMemoryFiles = originalFiles.Keys.Select(fileUri => fileUri.LocalPath);
      var inMemory = new InMemoryDirectoryInfoFromDotNet8(root, inMemoryFiles);
      return new CombinedDirectoryInfo([inMemory, OnDiskFileSystem.Instance.GetDirectoryInfoBase(root)]);
    }
  }
}
