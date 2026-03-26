#nullable enable
using DafnyCore.IncrementalCompilation;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.Dafny;

public class BlockStmt : BlockLikeStmt, ICloneable<BlockStmt> {

  public override List<Statement> Body { get; }

  public BlockStmt(Cloner cloner, BlockLikeStmt original) : base(cloner, original) {
    Body = original.Body.Select(stmt => cloner.CloneStmt(stmt, false)).ToList();
  }

  public BlockStmt(IOrigin origin, List<Statement> body, Attributes? attributes = null)
    : this(origin, body, [], attributes) {
  }

  [SyntaxConstructor]
  public BlockStmt(IOrigin origin, List<Statement> body, List<Label> labels, Attributes? attributes = null)
    : base(origin, labels, attributes) {
    Body = body;
  }

  public override void AppendStmt(Statement s) {
    Body.Add(s);
  }

  public override void Prepend(Statement s) {
    Body.Insert(0, s);
  }

  public new BlockStmt Clone(Cloner cloner) {
    return new BlockStmt(cloner, this);
  }

  protected BlockStmt(Protector protector, BlockStmt original, IEnumerable<Statement>? additional) : base(protector, original) {
    Body = [.. additional ?? [], .. original.Body.WithProtections(protector)];
  }

  public override BlockStmt WithProtections(Protector protector) => WithProtections(protector, null!);
  // the `additional` statements are added as-is, without any protections. if you want them to be protected, pass them protected
  public BlockStmt WithProtections(Protector protector, IEnumerable<Statement> additional) => new(protector, this, additional);
}