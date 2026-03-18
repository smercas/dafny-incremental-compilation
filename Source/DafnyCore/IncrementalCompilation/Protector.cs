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

    private Cloner cloner { get; }
    public Protector(Func<Cloner> clonerFactory) { cloner = clonerFactory(); }
    public Protector() { cloner = new(); }

    #region cloning
    public IOrigin Clone(IOrigin o) => cloner.Origin(o);
    [return: NotNullIfNotNull(nameof(a))] public Attributes? Clone(Attributes? a) => cloner.CloneAttributes(a);
    [return: NotNullIfNotNull(nameof(t))] public Microsoft.Dafny.Type? Clone(Microsoft.Dafny.Type? t) => cloner.CloneType(t);
    [return: NotNullIfNotNull(nameof(tp))] public TypeParameter? Clone(TypeParameter? tp) => cloner.CloneTypeParam(tp);
    [return: NotNullIfNotNull(nameof(t))] public AttributedToken? Clone(AttributedToken? t) => cloner.AttributedTok(t);
    [return: NotNullIfNotNull(nameof(e))] public E? Clone<E>(E? e) where E : Expression => cloner.CloneExpr(e) as E;
    public List<Label> Clone(List<Label> labels) => labels.ConvertAllWhere(label => (label.Name is not null, Clone(label)));
    public Label Clone(Label l) => l switch {
      AssertLabel al => Clone(al),
      _ when l.IsExactly() => new Label(Clone(l.Tok), l.Name),
      _ => throw new UnreachableException(),
    };
    #region Label
    public AssertLabel Clone(AssertLabel l) => new(Clone(l.Tok), l.Name);
    #endregion
    public Name Clone(Name n) => new(cloner, n);
    internal T Clone<T>(ICloneable<T> e) => e.Clone(cloner);
    #endregion

    #region other
    #endregion
  }
}
