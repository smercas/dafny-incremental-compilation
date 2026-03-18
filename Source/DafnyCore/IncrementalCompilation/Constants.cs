using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DafnyCore.IncrementalCompilation {
  internal static class Constants {
    public static string Name => "_IPM";
    public static string AttributeName => "ipm";
    public static string ImmediateAttributeName { get; } = $"{AttributeName}_now";
  }
}
