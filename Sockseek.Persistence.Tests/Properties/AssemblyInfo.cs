using Microsoft.VisualStudio.TestTools.UnitTesting;

// Intentional ceiling. Raising test workers previously caused async starvation
// and flaky timeouts when the solution's test hosts ran together.
[assembly: Parallelize(Workers = 6, Scope = ExecutionScope.ClassLevel)]
