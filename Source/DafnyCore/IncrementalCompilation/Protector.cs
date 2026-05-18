#nullable enable
using Microsoft.Dafny;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DafnyCore.IncrementalCompilation {
  public class Protector {
    #region protection context
    private Stack<(Lazy<MemberDecl>, Stack<Lazy<IAttributeBearingDeclaration>>)> Context { get; } = new();
    private class Box<T> {
      public T? Value { get; set; } = default;
    }
    public T WithMemberAdditionalContext<T>(Func<T> f) where T : MemberDecl {
      var r = new Box<T>();
      Context.Push((new(() => r.Value!), []));
      r.Value = f();
      Context.Pop();
      return r.Value;
    }

    public T WithAttributeAdditionalContext<T>(Func<T> f) where T : IAttributeBearingDeclaration {
      var r = new Box<T>();
      Context.Peek().Item2.Push(new(() => r.Value!));
      r.Value = f();
      Context.Peek().Item2.Pop();
      return r.Value;
    }
    public (Lazy<MemberDecl>, Stack<Lazy<IAttributeBearingDeclaration>>) MostRecentContext => Context.Peek();
    #endregion

    private Cloner Cloner { get; }
    public Protector(Func<Cloner> clonerFactory) { Cloner = clonerFactory(); }
    public Protector() { Cloner = new(); }

    #region cloning
    public IOrigin Clone(IOrigin o) => o == SourceOrigin.TokenForGeneratedLoopBody ? o : Cloner.Origin(o);
    [return: NotNullIfNotNull(nameof(a))] public Attributes? Clone(Attributes? a) => Cloner.CloneAttributes(a);
    [return: NotNullIfNotNull(nameof(t))] public Microsoft.Dafny.Type? Clone(Microsoft.Dafny.Type? t) => Cloner.CloneType(t);
    [return: NotNullIfNotNull(nameof(tp))] public TypeParameter? Clone(TypeParameter? tp) => Cloner.CloneTypeParam(tp);
    [return: NotNullIfNotNull(nameof(t))] public AttributedToken? Clone(AttributedToken? t) => Cloner.AttributedTok(t);
    [return: NotNullIfNotNull(nameof(e))] public E? Clone<E>(E? e) where E : Expression => Cloner.CloneExpr(e) as E;
    public List<Label> Clone(List<Label> labels) => labels.ConvertAllWhere(label => (label.Name is not null, Clone(label)));
    public Label Clone(Label l) => l switch {
      AssertLabel al => Clone(al),
      _ when l.IsExactly() => new Label(Clone(l.Tok), l.Name),
      _ => throw new UnreachableException(),
    };
    #region Label
    public AssertLabel Clone(AssertLabel l) => new(Clone(l.Tok), l.Name);
    #endregion
    public Name Clone(Name n) => new(Cloner, n);
    internal T Clone<T>(ICloneable<T> e) => e.Clone(Cloner);
    #endregion

    public void Protect(LiteralModuleDecl decl) {
      Protect(decl.ModuleDef.DefaultClass!);
      foreach (var module in decl.ModuleDef.PrefixNamedModules.Select(m => m.Module)) {
        Protect(module);
      }
      foreach (var sd in decl.ModuleDef.SourceDecls) {
        switch (sd) {
          case LiteralModuleDecl inner_lmd:
            Protect(inner_lmd);
            break;
          case ModuleExportDecl or AbstractModuleDecl or AliasModuleDecl:
            break; // nothing to be done on imports or exports
          case ModuleDecl:
            throw new UnreachableException();
          case IteratorDecl id:
            throw new NotImplementedException();
            break;
          case TopLevelDeclWithMembers wm when wm is (ClassDecl or TraitDecl or DatatypeDecl or NewtypeDecl or AbstractTypeDecl):
            Protect(wm);
            break;
          case SubsetTypeDecl tsd:
            throw new NotImplementedException();
            break;
          case ConcreteTypeSynonymDecl:
            break; // these are simply bare aliases
          default:
            throw new UnreachableException();
        }
      }
    }
    public void Protect(TopLevelDeclWithMembers decl) {
      decl.Members = decl.Members.ConvertAll(m => m is MethodOrFunction mof ? mof.WithProtections(this) : m);
      decl.SetMembersBeforeResolution();
    }
  }
}
