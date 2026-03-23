#nullable enable
using DafnyCore.IncrementalCompilation;
using System.Collections.Generic;
using System.Diagnostics.Contracts;

namespace Microsoft.Dafny;

public class ReturnStmt : ProduceStmt, ICloneable<ReturnStmt> {
  public bool ReverifyPost;  // set during pre-resolution refinement transformation

  public ReturnStmt Clone(Cloner cloner) {
    return new ReturnStmt(cloner, this);
  }

  public ReturnStmt(Cloner cloner, ReturnStmt original) : base(cloner, original) {
    ReverifyPost = original.ReverifyPost;
  }

  [SyntaxConstructor]
  public ReturnStmt(IOrigin origin, List<AssignmentRhs>? rhss, Attributes? attributes = null)
    : base(origin, rhss, attributes) {
    Contract.Requires(origin != null);
  }

  protected ReturnStmt(Protector protector, ReturnStmt original) : base(protector, original) {
    ReverifyPost = original.ReverifyPost;
  }
  public override ReturnStmt WithProtections(Protector protector) => new(protector, this);
}