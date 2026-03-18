using Microsoft.Dafny;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DafnyCore.IncrementalCompilation {
  public interface IProtectable<out T> where T : IProtectable<T> {
    T WithProtections(Protector protector);
  }
}
