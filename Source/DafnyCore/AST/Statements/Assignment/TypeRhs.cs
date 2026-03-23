#nullable enable
using DafnyCore.IncrementalCompilation;
using System.Collections.Generic;

namespace Microsoft.Dafny;

public abstract class TypeRhs : AssignmentRhs {

  [FilledInDuringResolution] public PreType? PreType = null!;
  [FilledInDuringResolution] public Type Type = null!;
  public bool WasResolved => Type != null;

  [SyntaxConstructor]
  protected TypeRhs(IOrigin origin, Attributes? attributes = null) : base(origin, attributes) {
  }

  protected TypeRhs(Cloner cloner, TypeRhs original)
    : base(cloner, original) {

    if (cloner.CloneResolvedFields) {
      Type = cloner.CloneType(original.Type);
    }
  }

  public IOrigin Start => Origin;


  public override IEnumerable<Statement> PreResolveSubStatements => [];

  protected TypeRhs(Protector protector, TypeRhs original) : base(protector, original) { }
  public abstract override AssignmentRhs WithProtections(Protector protector);
}