using System.Globalization;
using Parlot;
using Parlot.Fluent;
using PspScript.Ast;
using static Parlot.Fluent.Parsers;

namespace PspScript.Parser
{
    public static class Parser
    {
        private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
        {
            "game", "asset", "message", "struct", "actor", "state",
            "global", "level", "temp", "var", "fn",
            "init", "update", "draw", "exit", "on",
            "int", "u8", "u16", "u32", "f32", "bool", "void",
            "vec2", "vec3", "rect", "color", "timer", "ref",
            "if", "else", "while", "each", "in", "return",
            "send", "spawn", "destroy", "goto",
            "true", "false", "null", "self"
        };

        private static readonly HashSet<string> BuiltinTypes = new(StringComparer.Ordinal)
        {
            "int", "u8", "u16", "u32", "f32", "bool", "void",
            "vec2", "vec3", "rect", "color", "timer"
        };
    }
}