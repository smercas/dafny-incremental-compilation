#nullable enable
using DafnyCore.IncrementalCompilation;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.Dafny;

public class Method : MethodOrConstructor {
  private BlockStmt? body;
  public override List<Formal> Outs { get; }

  public override bool HasStaticKeyword { get; }
  public bool IsByMethod { get; }

  public Method(Cloner cloner, Method original) : base(cloner, original) {
    body = cloner.CloneBlockStmt(original.Body);
    Outs = original.Outs.ConvertAll(p => cloner.CloneFormal(p, false));
    HasStaticKeyword = original.HasStaticKeyword;
    IsByMethod = original.IsByMethod;
  }

  [SyntaxConstructor]
  public Method(IOrigin origin, Name nameNode, Attributes? attributes, bool hasStaticKeyword,
    bool isGhost, List<TypeParameter> typeArgs, List<Formal> ins, List<AttributedExpression> req,
    List<AttributedExpression> ens, Specification<FrameExpression> reads, Specification<Expression> decreases,
    List<Formal> outs, Specification<FrameExpression> mod, BlockStmt? body, IOrigin? signatureEllipsis, bool isByMethod = false)
    : base(origin, nameNode, attributes, isGhost, typeArgs, ins, req, ens, reads, decreases, mod, signatureEllipsis) {
    this.body = body;
    IsByMethod = isByMethod;
    Outs = outs;
    HasStaticKeyword = hasStaticKeyword;
  }

  public override BlockStmt? Body => body;
  public override void SetBody(BlockLikeStmt newBody) {
    body = (BlockStmt?)newBody;
  }

  protected Method(Protector protector, Method original) : base(protector, original) {
    body = original.Body?.WithProtections(protector);
    Outs = original.Outs.ConvertAll(o => o.WithProtections(protector));
    HasStaticKeyword = original.HasStaticKeyword;
    IsByMethod = original.IsByMethod;
    Ens.InsertRange(0, original.Outs.Select(ProtectorExtensions.ToProtectClause));
  }
  public override Method WithProtections(Protector protector) =>
    protector.WithMemberAdditionalContext(() => new Method(protector, this));
}