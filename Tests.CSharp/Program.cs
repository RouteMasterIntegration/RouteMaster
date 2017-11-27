using System;
using Expecto.CSharp;

namespace Tests.CSharp
{
    class Program
    {
        static int Main(string[] args)
        {
            return Runner.RunTestsInAssembly(Runner.DefaultConfig, args);
        }
    }
}
