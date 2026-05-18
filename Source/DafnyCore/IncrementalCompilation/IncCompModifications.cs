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
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Microsoft.Dafny;

public interface IChangeKind;
public interface IWellFormedness : IChangeKind;
public interface IProofHint : IChangeKind;
public interface IInvariantRelated : IChangeKind;
public interface IInvariantInitial : IInvariantRelated;
public interface IInvariantMaintain : IInvariantRelated;


public abstract class Parsed(int entryPoint, string text) {
  protected abstract string Type { get; }
  private int entryPoint { get; } = entryPoint;
  public string Text { get; } = text;

  public class InvalidEntryPoint(int entryPoint) : Exception($"{entryPoint} is not a valid entry point (there {(ProtectToProveApplySuffix.ChangesPerEntryPoint.Count == 1 ? "is" : "are")} only {ProtectToProveApplySuffix.ChangesPerEntryPoint.Count} entry {(ProtectToProveApplySuffix.ChangesPerEntryPoint.Count == 1 ? "point" : "points")})");
  public class InvalidBranchIndex(int branchIndex, int branchCount) : Exception($"{branchIndex} is not a valid branch for this loop (there {(branchCount == 1 ? "is" : "are")} only {branchCount} {(branchCount == 1 ? "branch" : "branches")})");
  public class NotFound(int entryPoint, string type) : Exception($"entry point {entryPoint} does not accept {type} changes");
  public Change MatchChange() {
    if (!(entryPoint < ProtectToProveApplySuffix.ChangesPerEntryPoint.Count)) { throw new InvalidEntryPoint(entryPoint); }
    return MatchChangeFrom(ProtectToProveApplySuffix.ChangesPerEntryPoint[entryPoint]) switch {
      null => throw new NotFound(entryPoint, Type),
      var change => change,
    };
  }
  protected abstract Change? MatchChangeFrom(IReadOnlyList<Change> changes);
}
public class WellFormedness(int entryPoint, string text) : Parsed(entryPoint, text) {
  protected override string Type { get; } = "well-formedness";
  protected override Change? MatchChangeFrom(IReadOnlyList<Change> changes) => changes.Where(c => c is IWellFormedness).ToList() switch {
    [] => null,
    [var change] => change,
    [_, ..] => throw new UnreachableException("changes per entry point are not supposed to have more than one of each type"),
  };
}
public class ProofHint(int entryPoint, string text) : Parsed(entryPoint, text) {
  protected override string Type { get; } = "proof hint";
  protected override Change? MatchChangeFrom(IReadOnlyList<Change> changes) => changes.Where(c => c is IProofHint).ToList() switch {
    [] => null,
    [var change] => change,
    [_, ..] => throw new UnreachableException("changes per entry point are not supposed to have more than one of each type"),
  };
}
public class InvariantInitial(int entryPoint, string text) : Parsed(entryPoint, text) {
  protected override string Type { get; } = "invariant initialisation proof hint";
  protected override Change? MatchChangeFrom(IReadOnlyList<Change> changes) => changes.Where(c => c is IInvariantInitial).ToList() switch {
    [] => null,
    [var change] => change,
    [_, ..] => throw new UnreachableException("changes per entry point are not supposed to have more than one of each type"),
  };
}
public class InvariantMaintain(int entryPoint, string text, int branchIndex) : Parsed(entryPoint, text) {
  protected override string Type { get; } = $"branch {branchIndex} invariant maintenance proof hint";
  private int branchIndex { get; } = branchIndex;
  protected override Change? MatchChangeFrom(IReadOnlyList<Change> changes) {
    return changes.Where(c => c is IInvariantMaintain).ToList() switch {
      [] => null,
      [AlternativeLoopInvariantMaintainProofChange { BranchIndex: var branchIndex } change] when branchIndex == this.branchIndex => change,
      [AlternativeLoopInvariantMaintainProofChange { BranchCount: var count }] => throw new InvalidBranchIndex(branchIndex, count),
      [OneBodyLoopInvariantMaintainProofChange change] when branchIndex == 0 => change,
      [BodylessLoopInvariantMaintainProofChange change] when branchIndex == 0 => change,
      [OneBodyLoopInvariantMaintainProofChange or BodylessLoopInvariantMaintainProofChange] => throw new InvalidBranchIndex(branchIndex, 1),
      var filtered when filtered.OfType<AlternativeLoopInvariantMaintainProofChange>().ToList() is var typed &&
                        typed.Count == filtered.Count &&
                        typed[0].BranchCount is var branchCount &&
                        typed.Skip(1).All(c => c.BranchCount == branchCount) &&
                        typed.All(c => c.BranchIndex < branchCount) && typed.Count == branchCount && typed.Select(c => c.BranchIndex).Distinct().Count() == branchCount // should mean that all elements have an index from 0 to branchCount - 1 and only one such index is present in all of the,
                        => typed.First(c => c.BranchIndex == branchIndex),
      _ => throw new UnreachableException("invariant maintain changes for alternative statement is malformed"),
    };
  }
}
public static partial class Patterns {
  [GeneratedRegex(@"^(?<entryPoint>0|[1-9]\d*)wf: (?<text>.*)$")]
  private static partial Regex WellFormednessPattern();

  [GeneratedRegex(@"^(?<entryPoint>0|[1-9]\d*)ph: (?<text>.*)$")]
  private static partial Regex ProofHintPattern();

  [GeneratedRegex(@"^(?<entryPoint>0|[1-9]\d*)ii: (?<text>.*)$")]
  private static partial Regex InvariantInitialPattern();

  [GeneratedRegex(@"^(?<entryPoint>0|[1-9]\d*)im(?<branchIndex>0|[1-9]\d*)?: (?<text>.*)$")]
  private static partial Regex InvariantMaintainPattern();

  public static Parsed? Parse(string text) {
    if (WellFormednessPattern().Match(text) is { Success: true } wf) {
      return new WellFormedness(
        int.Parse(wf.Groups["entryPoint"].Value),
        wf.Groups["text"].Value
      );
    }
    if (ProofHintPattern().Match(text) is { Success: true } ph) {
      return new ProofHint(
        int.Parse(ph.Groups["entryPoint"].Value),
        ph.Groups["text"].Value
      );
    }
    if (InvariantInitialPattern().Match(text) is { Success: true } ii) {
      return new InvariantInitial(
        int.Parse(ii.Groups["entryPoint"].Value),
        ii.Groups["text"].Value
      );
    }
    if (InvariantMaintainPattern().Match(text) is { Success: true } im) {
      var branchIndexGroup = im.Groups["branchIndex"];
      return new InvariantMaintain(
        int.Parse(im.Groups["entryPoint"].Value),
        im.Groups["text"].Value,
        branchIndexGroup.Length > 0 ? int.Parse(branchIndexGroup.Value) : 0
      );
    }
    return null;
  }
}

public abstract class Change(Uri uri, Range range) {
  public static Comparer<(Uri Uri, Range Range, string Text)> Comparer { get; } = Comparer<(Uri Uri, Range Range, string Text)>.Create(static (l, r) => { // IPMTODO: revisit when testing
    static bool Consecutive(Position first, params Position[] positions) =>
      positions.SkipLast(1).Zip(positions.Skip(1)).All(p => p.First <= p.Second);
    var (ls, le) = (l.Range.Start, l.Range.End);
    var (rs, re) = (r.Range.Start, r.Range.End);
    if (l.Uri != r.Uri) {
      throw new InvalidOperationException($"Comparison between ChangesPerEntryPoint from different files is invalid (l = {l.Uri}, r = {r.Uri})");
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

  public static bool IsEmptyBody(Statement? s) => s is null || ReferenceEquals(s.Origin, SourceOrigin.TokenForGeneratedLoopBody);

  public Uri Uri { get; } = uri;
  public abstract ModuleDecl AffectedModuleDecl { get; }
  public Range Range { get; } = range;
  public virtual string? Text { get; set; } = null;
  public bool IsEmptyChange => Text is null or "";

  protected static Range RangeFor(Token start, Token? end = null) {
    if (end is null) { end = start; }
    return new(start.line, start.col, end.line, end.col);
  }
}
public interface IChangeToMemberDecl { public MemberDecl MemberDecl { get; } }

public abstract class ChangeToMemberDecl<MD>(MD memberDecl, Range range) : Change(memberDecl.Origin.Uri, range), IChangeToMemberDecl where MD : MemberDecl {
  public MD MemberDecl { get; private set; } = memberDecl;
  MemberDecl IChangeToMemberDecl.MemberDecl => MemberDecl;
  public override ModuleDecl AffectedModuleDecl => MemberDecl.EnclosingClass.EnclosingModuleDefinition.EnclosingLiteralModuleDecl!;

  public void Update(MD memberDecl) {
    MemberDecl = memberDecl;
  }
}
public class EnsuresWFChange(MemberDecl memberDecl, AttributedExpression attributedExpression) :
  ChangeToMemberDecl<MemberDecl>(memberDecl, RangeFor(attributedExpression)), IWellFormedness {
  private static Range RangeFor(AttributedExpression attributedExpression) => RangeFor(attributedExpression.StartToken);
}

public abstract class EnsuresMethodOrConstructorOrFunctionProofHintChange<MOF>(MOF methodOrFunction, Range range) :
  ChangeToMemberDecl<MOF>(methodOrFunction, range), IProofHint
  where MOF : MethodOrFunction;
public class MethodOrConstructorWithBodyEnsuresProofHintChange(MethodOrConstructor methodOrConstructor) :
  EnsuresMethodOrConstructorOrFunctionProofHintChange<MethodOrConstructor>(methodOrConstructor, RangeFor(methodOrConstructor)) {
  private static Range RangeFor(MethodOrConstructor methodOrConstructor) => RangeFor(!IsEmptyBody(methodOrConstructor.Body) ? methodOrConstructor.Body!.EndToken : throw new UnreachableException($"method is meant to have a body, use {typeof(BodylessMethodOrConstructorEnsuresProofHintChange).Name} instead"));
}
public class BodylessMethodOrConstructorEnsuresProofHintChange(MethodOrConstructor methodOrConstructor) :
  EnsuresMethodOrConstructorOrFunctionProofHintChange<MethodOrConstructor>(methodOrConstructor, RangeFor(methodOrConstructor)) {
  private static Range RangeFor(MethodOrConstructor methodOrConstructor) => RangeFor(IsEmptyBody(methodOrConstructor.Body) ? methodOrConstructor.EndToken.Next : throw new UnreachableException($"method is meant to not have a body, use {typeof(MethodOrConstructorWithBodyEnsuresProofHintChange).Name} instead"));
}

public abstract class EnsuresStatementProofHintChange(MemberDecl memberDecl, Range range) :
  ChangeToMemberDecl<MemberDecl>(memberDecl, range), IProofHint { }

public abstract class EnsuresForallStatementProofHintChange(MemberDecl memberDecl, Range range) :
  EnsuresStatementProofHintChange(memberDecl, range), IProofHint;
public class EnsuresForallStatementWithBodyProofHintChange(MemberDecl memberDecl, ForallStmt containingStmt) :
  EnsuresForallStatementProofHintChange(memberDecl, RangeFor(containingStmt)) {
  private static Range RangeFor(ForallStmt containingStmt) => RangeFor(!IsEmptyBody(containingStmt.Body) ? containingStmt.Body!.EndToken : throw new UnreachableException($"forall statement is meant to have a body, use {typeof(EnsuresBodylessForallStatementProofHintChange).Name} instead")); // IPMTODO: check if this is ok
}
public class EnsuresBodylessForallStatementProofHintChange(MemberDecl memberDecl, ForallStmt containingStmt) :
  EnsuresForallStatementProofHintChange(memberDecl, RangeFor(containingStmt)) {
  private static Range RangeFor(ForallStmt containingStmt) => RangeFor(IsEmptyBody(containingStmt.Body) ? containingStmt.EndToken.Next : throw new UnreachableException($"forall statement is meant to not have a body, use {typeof(EnsuresForallStatementWithBodyProofHintChange).Name} instead")); // IPMTODO: check if this is ok
}
public class EnsuresOpaqueBlockProofHintChange(MemberDecl memberDecl, OpaqueBlock containingStmt) :
  EnsuresStatementProofHintChange(memberDecl, RangeFor(containingStmt)), IProofHint {
  private static Range RangeFor(OpaqueBlock containingStmt) => RangeFor(containingStmt.EndToken); // IPMTODO: check if correct
}

public class FunctionEnsuresProofHintChange(Function function, AttributedExpression attributedExpression) :
  EnsuresMethodOrConstructorOrFunctionProofHintChange<Function>(function, RangeFor(attributedExpression)), IProofHint {
  private static Range RangeFor(AttributedExpression attributedExpression) => RangeFor(attributedExpression.StartToken);
}

public class InvariantWFChange(MemberDecl memberDecl, AttributedExpression attributedExpression) :
  ChangeToMemberDecl<MemberDecl>(memberDecl, RangeFor(attributedExpression)), IWellFormedness {
  private static Range RangeFor(AttributedExpression attributedExpression) => RangeFor(attributedExpression.StartToken);
}
public class InvariantInitialProofChange(MemberDecl memberDecl, LoopStmt loopStmt) :
  ChangeToMemberDecl<MemberDecl>(memberDecl, RangeFor(loopStmt)), IInvariantInitial {
  private static Range RangeFor(LoopStmt loopStmt) => RangeFor(loopStmt.StartToken);
}
public abstract class InvariantMaintainProofChange(MemberDecl memberDecl, Range range) :
  ChangeToMemberDecl<MemberDecl>(memberDecl, range), IInvariantMaintain {
  private static Range RangeFor(LoopStmt loopStmt) => RangeFor(loopStmt.EndToken); // IPMTODO: see if this works as intended
}

public class AlternativeLoopInvariantMaintainProofChange(MemberDecl memberDecl, AlternativeLoopStmt loopStmt, int index) :
  InvariantMaintainProofChange(memberDecl, RangeFor(loopStmt, index)) {
  public int BranchCount { get; } = loopStmt.Alternatives.Count;
  public int BranchIndex { get; } = index;

  private static Range RangeFor(AlternativeLoopStmt loopStmt, int index) => RangeFor(loopStmt.Alternatives[index].EndToken); // IPMTODO: see if this works as intended
}


// the loop body needs to be nonEmpty aka `loopStmt.Body is not null`
public class OneBodyLoopInvariantMaintainProofChange(MemberDecl memberDecl, OneBodyLoopStmt loopStmt) :
  InvariantMaintainProofChange(memberDecl, RangeFor(loopStmt)) {
  private static Range RangeFor(OneBodyLoopStmt loopStmt) => RangeFor(!IsEmptyBody(loopStmt.Body) ? loopStmt.Body!.EndToken : throw new UnreachableException($"loop statement is meant to have a body, use {typeof(BodylessLoopInvariantMaintainProofChange).Name} instead"));
}

public class BodylessLoopInvariantMaintainProofChange(MemberDecl memberDecl, OneBodyLoopStmt loopStmt) :
  InvariantMaintainProofChange(memberDecl, RangeFor(loopStmt)) {
  private static Range RangeFor(OneBodyLoopStmt loopStmt) => RangeFor(IsEmptyBody(loopStmt.Body) ? loopStmt.EndToken.Next : throw new UnreachableException($"loop statement is meant to not have a body, use {typeof(OneBodyLoopInvariantMaintainProofChange).Name} instead"));
}

public class AssertWFChange(MemberDecl memberDecl, AssertStmt assertStmt) :
  ChangeToMemberDecl<MemberDecl>(memberDecl, RangeFor(assertStmt)), IWellFormedness {
  private static Range RangeFor(AssertStmt stmt) => RangeFor(stmt.Expr.StartToken);
}
public abstract class AssertProofHintChangeBase(MemberDecl memberDecl, Range range) :
  ChangeToMemberDecl<MemberDecl>(memberDecl, range), IProofHint;
public class AssertWithoutByProofHintChange(MemberDecl memberDecl, AssertStmt assertStmt) :
  AssertProofHintChangeBase(memberDecl, RangeFor(assertStmt)) {
  private static Range RangeFor(AssertStmt stmt) => new(
    stmt.EndToken.line, stmt.EndToken.col,
    stmt.EndToken.line, stmt.EndToken.col + 1
  ); // IPMTODO: see if can be replaced by RangeFor(stmt.EndToken, stmt.EndToken.Next);
  private string? text { get; set; }
  public override string? Text { get => text; set { text = value is null ? null : $" by {{ {value} }}"; } }
}
public class AssertWithByProofHintChange(MemberDecl memberDecl, BlockByProofStmt blockByProofStmt) :
  AssertProofHintChangeBase(memberDecl, RangeFor(blockByProofStmt)) {
  private static Range RangeFor(BlockByProofStmt blockByProofStmt) => RangeFor(blockByProofStmt.Proof.EndToken);
}
