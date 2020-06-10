
using System;

namespace Rollback
{
    public static class Debug 
    {
        public static void Assert(bool condition, string message)
        {
            if (condition) return;
            throw new Exception($"ASSERTION FAILED: {message}");
        }
    }
}
