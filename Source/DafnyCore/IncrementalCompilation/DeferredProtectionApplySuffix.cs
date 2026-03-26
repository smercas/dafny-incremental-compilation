#nullable enable
using Microsoft.Dafny;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DafnyCore.IncrementalCompilation {
  // this needs to be treated differently from applySUffix, since it's basically two ApplySuffix expressions in one
  public class DeferredProtectionApplySuffix : ApplySuffix, ICloneable<DeferredProtectionApplySuffix> {
    public sealed class ReplaceReporter : IDisposable {
      private ModuleResolver Resolver { get; }
      private BatchErrorReporter PrevReporter { get; }
      public ReplaceReporter(ModuleResolver resolver) {
        Resolver = resolver;
        (PrevReporter, resolver.reporter) = ((resolver.Reporter as BatchErrorReporter)!, new BatchErrorReporter(resolver.Reporter.Options));
      }

      public bool NoErrorsYet => Resolver.Reporter.ErrorCount == 0;

      public void Dispose() {
        if (NoErrorsYet) {
          foreach (var diagnostic in (Resolver.Reporter as BatchErrorReporter)!.AllMessages) {
            (Resolver.Reporter as BatchErrorReporter)!.MessageCore(diagnostic);
          }
        }
        Resolver.reporter = PrevReporter;
      }
    }
    public Expression ProtectedLhs { get; private set; }
    public Expression UnprotectedLhs { get; private set; }
    public DeferredProtectionApplySuffix(Cloner cloner, DeferredProtectionApplySuffix original) : base(cloner, original) {
      if ((original.Lhs, original.Bindings) is (null, null)) {
        ProtectedLhs = cloner.CloneExpr(original.ProtectedLhs);
        UnprotectedLhs = cloner.CloneExpr(original.UnprotectedLhs);
      } else if (ReferenceEquals(original.Lhs, original.UnprotectedLhs)) {
        ProtectedLhs = cloner.CloneExpr(original.ProtectedLhs);
        UnprotectedLhs = Lhs;
      } else if (ReferenceEquals(original.Lhs, original.ProtectedLhs)) {
        ProtectedLhs = Lhs;
        UnprotectedLhs = cloner.CloneExpr(original.UnprotectedLhs);
      } else { throw new UnreachableException(); }
    }

    public DeferredProtectionApplySuffix(IOrigin origin, IOrigin? atTok, (Expression Cloned, Expression Protected) lhs, ActualBindings bindings, Token? closeParen)
    : base(origin, atTok, null!, bindings, closeParen) {
      (ProtectedLhs, UnprotectedLhs) = lhs;
    }

    public new DeferredProtectionApplySuffix Clone(Cloner cloner) => new(cloner, this);
  }
}
