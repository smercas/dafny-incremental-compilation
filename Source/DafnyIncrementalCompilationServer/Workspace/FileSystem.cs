using DafnyCore.Options;
using Microsoft.Dafny.LanguageServer.Workspace;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Microsoft.Extensions.Logging;
using System;
using System.Collections;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Dafny.IncrementalCompilation.Workspace {
  public class FileSystem : IFileSystem {
    private class Entry(TextBuffer buffer, int? version) {
      public TextBuffer Buffer { get; set; } = buffer;
      public int? Version { get; set; } = version; // TODO: see if only increments are necessary in this context
    }
    private readonly ILogger<FileSystem> logger;
    private readonly FrozenDictionary<Uri, Entry> files;

    private class UriToBufferDict(IReadOnlyDictionary<Uri, Entry> d) : IReadOnlyDictionary<Uri, TextBuffer> {
      TextBuffer IReadOnlyDictionary<Uri, TextBuffer>.this[Uri key] => d[key].Buffer;
      IEnumerable<Uri> IReadOnlyDictionary<Uri, TextBuffer>.Keys => d.Keys;
      IEnumerable<TextBuffer> IReadOnlyDictionary<Uri, TextBuffer>.Values => d.Values.Select(e => e.Buffer);
      int IReadOnlyCollection<KeyValuePair<Uri, TextBuffer>>.Count => d.Count;
      bool IReadOnlyDictionary<Uri, TextBuffer>.ContainsKey(Uri key) => d.ContainsKey(key);
      IEnumerator<KeyValuePair<Uri, TextBuffer>> IEnumerable<KeyValuePair<Uri, TextBuffer>>.GetEnumerator() {
        foreach (var (uri, entry) in d) { yield return new KeyValuePair<Uri, TextBuffer>(uri, entry.Buffer); }
      }
      IEnumerator IEnumerable.GetEnumerator() => (this as IEnumerable<KeyValuePair<Uri, TextBuffer>>).GetEnumerator();
      bool IReadOnlyDictionary<Uri, TextBuffer>.TryGetValue(Uri key, out TextBuffer value) {
        if (d.TryGetValue(key, out var entry)) {
          value = entry.Buffer;
          return true;
        }
        value = default!;
        return false;
      }
    }
    public readonly IReadOnlyDictionary<Uri, TextBuffer> Buffers;

    public FileSystem(ILogger<FileSystem> logger, IReadOnlyCollection<Uri> files) {
      Contract.Assert(Contract.ForAll(files, file => File.Exists(file.LocalPath)));
      this.logger = logger;
      this.files = files.Select(uri => KeyValuePair.Create(uri, new Entry(new(File.ReadAllText(uri.LocalPath)), null))).ToFrozenDictionary();
      Buffers = new UriToBufferDict(this.files);
    }

    public void ApplyModification(IncCompModification modification) {
      //TODO: this and the changes system at large
    }

    public FileSnapshot ReadFile(Uri uri) {
      if (files.TryGetValue(uri, out var entry)) {
        return new FileSnapshot(new StringReader(entry.Buffer.Text), entry.Version);
      }

      return OnDiskFileSystem.Instance.ReadFile(uri);
    }
    public bool Exists(Uri path) => files.ContainsKey(path) || OnDiskFileSystem.Instance.Exists(path);
    public DirectoryInfoBase GetDirectoryInfoBase(string root) {
      var inMemoryFiles = files.Keys.Select(fileUri => fileUri.LocalPath);
      var inMemory = new InMemoryDirectoryInfoFromDotNet8(root, inMemoryFiles);
      return new CombinedDirectoryInfo([inMemory, OnDiskFileSystem.Instance.GetDirectoryInfoBase(root)]);
    }
  }
}
