using DafnyCore.IncrementalCompilation;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq;

namespace Microsoft.Dafny;

public class GuardedAlternative : NodeWithOrigin, IAttributeBearingDeclaration, IProtectable<GuardedAlternative> {
  public bool IsBindingGuard;
  public Expression Guard;
  public List<Statement> Body;
  public Attributes Attributes { get; set; }
  string IAttributeBearingDeclaration.WhatKind => "alternative-based case";
  public override IEnumerable<INode> Children => Attributes.AsEnumerable().
    Concat<Node>(new List<Node>() { Guard }).Concat<Node>(Body);
  public override IEnumerable<INode> PreResolveChildren => Children;

  [ContractInvariantMethod]
  void ObjectInvariant() {
    Contract.Invariant(Origin != null);
    Contract.Invariant(Guard != null);
    Contract.Invariant(!IsBindingGuard || (Guard is ExistsExpr && ((ExistsExpr)Guard).Range == null));
    Contract.Invariant(Body != null);
  }
  public GuardedAlternative(IOrigin origin, bool isBindingGuard, Expression guard, List<Statement> body) : base(origin) {
    Contract.Requires(origin != null);
    Contract.Requires(guard != null);
    Contract.Requires(!isBindingGuard || (guard is ExistsExpr && ((ExistsExpr)guard).Range == null));
    Contract.Requires(body != null);
    this.IsBindingGuard = isBindingGuard;
    this.Guard = guard;
    this.Body = body;
    this.Attributes = null;
  }
  public GuardedAlternative(IOrigin origin, bool isBindingGuard, Expression guard, List<Statement> body, Attributes attrs) : base(origin) {
    Contract.Requires(origin != null);
    Contract.Requires(guard != null);
    Contract.Requires(!isBindingGuard || (guard is ExistsExpr && ((ExistsExpr)guard).Range == null));
    Contract.Requires(body != null);
    this.IsBindingGuard = isBindingGuard;
    this.Guard = guard;
    this.Body = body;
    this.Attributes = attrs;
  }

  protected GuardedAlternative(Protector protector, GuardedAlternative original) : base(protector, original) {
    IsBindingGuard = original.IsBindingGuard;
    (Guard, Body) = (original.IsBindingGuard, original.Guard) switch {
      (false, _) => (
        original.Guard.WithProtections(protector),
        original.Body.WithProtections(protector).ToList()
      ),
      (true, ExistsExpr { Range: null } guard) => (
        guard.WithProtections(protector, ComprehensionExpr.Options.Empty),
        [.. guard.BoundVars.Select(bv => bv.ToProtectAssertion()), .. original.Body.WithProtections(protector)]
      ),
      _ => throw new UnreachableException(),
    };
    Attributes = protector.Clone(original.Attributes);
  }
  public GuardedAlternative WithProtections(Protector protector) => new(protector, this);
}