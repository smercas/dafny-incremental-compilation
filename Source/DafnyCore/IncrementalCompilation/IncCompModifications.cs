#nullable enable
using Dafny;
using DafnyCore.IncrementalCompilation;
using OmniSharp.Extensions.JsonRpc.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Security.Policy;
using System.Text;
using System.Threading.Tasks;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Microsoft.Dafny;

public abstract class ChangeKind;
public sealed class WF : ChangeKind;
public sealed class ProofHint : ChangeKind;
public abstract class Change(Uri uri, Range range) {
  public static Comparer<Change> Comparer { get; } = Comparer<Change>.Create(static (l, r) => { // TODO: revisit when testing
    static bool Consecutive(Position first, params Position[] positions) =>
      positions.SkipLast(1).Zip(positions.Skip(1)).All(p => p.First <= p.Second);
    var (ls, le) = (l.Range.Start, l.Range.End);
    var (rs, re) = (r.Range.Start, r.Range.End);
    if (l.Uri != r.Uri) {
      throw new InvalidOperationException($"Comparison between Changes from different files is invalid (l = {l.Uri}, r = {r.Uri})");
    }
    if (l == r) { return 0; }
    if (Consecutive(ls, le, rs, re)) {
      return -1;
    }
    if (Consecutive(rs, re, ls, le)) {
      return 1;
    }
    throw new UnreachableException($"ranges can't overlap due to how they're constructed, but they do here: l = [{ls}, {le}], r = [{rs}, {re}]");
    //int cmp = string.Compare(l.Origin.Uri.AbsoluteUri, r.Origin.Uri.AbsoluteUri);
    //if (cmp != 0) { return cmp; }
    //cmp = l.Origin.line.CompareTo(r.Origin.line);
    //if (cmp != 0) { return cmp; }
    //  ;
    //return l.Origin.col.CompareTo(r.Origin.col);
  });
  public Uri Uri { get; } = uri;
  public abstract ModuleDecl AffectedModuleDecl { get; }
  public Range Range { get; } = range;
  public virtual string? Text { get; set; } = null;
  public bool IsEmptyChange => Text is null;
}
public abstract class Change<CK>(Uri uri, Range range) : Change(uri, range) where CK : ChangeKind;
public abstract class ChangeToMemberDecl<CK, MD, ABD>(MD memberDecl, Uri uri, Range range) : Change<CK>(uri, range) where CK : ChangeKind where MD : MemberDecl where ABD : IAttributeBearingDeclaration {
  public MD MemberDecl { get; private set; } = memberDecl;
  public override ModuleDecl AffectedModuleDecl => MemberDecl.EnclosingClass.EnclosingModuleDefinition.EnclosingLiteralModuleDecl!;

  public virtual void Update(MD memberDecl, ABD attributeBearingDeclaration) {
    MemberDecl = memberDecl;
  }
}
public class EnsuresWFChange(MemberDecl memberDecl, AttributedExpression attributedExpression) : ChangeToMemberDecl<WF, MemberDecl, AttributedExpression>(memberDecl, UriFor(attributedExpression), RangeFor(attributedExpression)) {
  private static Uri UriFor(AttributedExpression attributedExpression) => attributedExpression.Origin.Uri;
  private static Range RangeFor(AttributedExpression attributedExpression) => new(
    attributedExpression.StartToken.line, attributedExpression.StartToken.col,
    attributedExpression.StartToken.line, attributedExpression.StartToken.col
  );
  public override void Update(MemberDecl memberDecl, AttributedExpression attributedExpression) {
    base.Update(memberDecl, attributedExpression);
  }
}
public abstract class EnsuresMethodOrFunctionProofHintChange<MOF>(MOF methodOrFunction, Uri uri, Range range) : ChangeToMemberDecl<ProofHint, MOF, AttributedExpression>(methodOrFunction, uri, range) where MOF : MethodOrFunction;
public class MethodOrConstructorEnsuresProofHintChange(MethodOrConstructor methodOrConstructor) : EnsuresMethodOrFunctionProofHintChange<MethodOrConstructor>(methodOrConstructor, UriFor(methodOrConstructor), RangeFor(methodOrConstructor)) {
  private static Uri UriFor(MethodOrConstructor methodOrConstructor) => methodOrConstructor.Origin.Uri;
  private static Range RangeFor(MethodOrConstructor methodOrConstructor) => new(
    methodOrConstructor.EndToken.line, methodOrConstructor.EndToken.col,
    methodOrConstructor.EndToken.line, methodOrConstructor.EndToken.col
  );
  public override void Update(MethodOrConstructor methodOrConstructor, AttributedExpression attributedExpression) {
    base.Update(methodOrConstructor, attributedExpression);
  }
}
public class FunctionEnsuresProofHintChange(Function function, AttributedExpression attributedExpression) : EnsuresMethodOrFunctionProofHintChange<Function>(function, UriFor(function), RangeFor(attributedExpression)) {
  private static Uri UriFor(Function function) => function.Origin.Uri;
  private static Range RangeFor(AttributedExpression attributedExpression) => new(
    attributedExpression.StartToken.line, attributedExpression.StartToken.col,
    attributedExpression.StartToken.line, attributedExpression.StartToken.col
  );
  public override void Update(Function methodOrConstructor, AttributedExpression attributedExpression) {
    base.Update(methodOrConstructor, attributedExpression);
  }
}
public class AssertWFChange(MemberDecl memberDecl, AssertStmt assertStmt) : ChangeToMemberDecl<WF, MemberDecl, AssertStmt>(memberDecl, UriFor(assertStmt), RangeFor(assertStmt)) {
  private static Uri UriFor(AssertStmt assertStmt) => assertStmt.Origin.Uri;
  private static Range RangeFor(AssertStmt stmt) => new(
    stmt.Expr.StartToken.line, stmt.Expr.StartToken.col,
    stmt.Expr.StartToken.line, stmt.Expr.StartToken.col
  );
  public override void Update(MemberDecl memberDecl, AssertStmt assertStmt) {
    base.Update(memberDecl, assertStmt);
  }
}
public abstract class AssertProofHintChangeBase<ABD>(MemberDecl memberDecl, Uri uri, Range range) : ChangeToMemberDecl<ProofHint, MemberDecl, ABD>(memberDecl, uri, range) where ABD : IAttributeBearingDeclaration;
public class AssertWithoutByProofHintChange(MemberDecl memberDecl, AssertStmt assertStmt) : AssertProofHintChangeBase<AssertStmt>(memberDecl, UriFor(assertStmt), RangeFor(assertStmt)) {
  private static Uri UriFor(AssertStmt assertStmt) => assertStmt.Origin.Uri;
  private static Range RangeFor(AssertStmt stmt) => new(
    stmt.EndToken.line, stmt.EndToken.col,
    stmt.EndToken.line, stmt.EndToken.col + 1
  );
  public override void Update(MemberDecl memberDecl, AssertStmt assertStmt) {
    base.Update(memberDecl, assertStmt);
  }
  private string? text { get; set; }
  public override string? Text { get => text; set { text = value is null ? null : $" by {{ {value} }}"; } }
}
public class AssertWithByProofHintChange(MemberDecl memberDecl, BlockByProofStmt blockByProofStmt) : AssertProofHintChangeBase<BlockByProofStmt>(memberDecl, UriFor(blockByProofStmt), RangeFor(blockByProofStmt)) {
  private static Uri UriFor(BlockByProofStmt blockByProofStmt) => blockByProofStmt.Origin.Uri;
  private static Range RangeFor(BlockByProofStmt blockByProofStmt) => new(
    blockByProofStmt.Proof.EndToken.line, blockByProofStmt.Proof.EndToken.col,
    blockByProofStmt.Proof.EndToken.line, blockByProofStmt.Proof.EndToken.col
  );
  public override void Update(MemberDecl memberDecl, BlockByProofStmt blockByProofStmt) {
    base.Update(memberDecl, blockByProofStmt);
  }
}

public abstract class IncCompModifications {
}

// the scope of modifications most likely will always be constrained to that of a module declaration
public abstract class ModificationToModuleDeclaration : IncCompModifications {
  public class Pair<T>(T prev) where T : class {
    public T Old { get; private init; } = prev;
    private T? newlyProcessed = null;
    public T NewlyProcessed {
      get {
        Contract.Requires(newlyProcessed != null);
        return newlyProcessed!;
      }
      set {
        Contract.Requires(newlyProcessed == null);
        newlyProcessed = value;
      }
    }
  }
  public abstract Pair<ModuleDecl> AffectedModuleDecl { get; }
  public bool HasBeenProcessed => AffectedModuleDecl.NewlyProcessed != null;
}

public class AppendStatementToMethod(Method method) : ModificationToModuleDeclaration {
  private Pair<ModuleDecl> affectedModuleDecl { get; } = new(method.EnclosingClass.EnclosingModuleDefinition.EnclosingLiteralModuleDecl!);
  public override Pair<ModuleDecl> AffectedModuleDecl => affectedModuleDecl;
  public Method Method => method;
}
