using System.Text.RegularExpressions;

namespace Asobu.Core.Launch;

/// <summary>
/// Telling Java not to compile one method.
///
/// Java compiles the code it is running as it runs it, on threads of its own, and that compiler
/// can crash — when it does it takes the whole process with it, which is why nothing in the game
/// reports it and no crash report is written. The error file Java leaves behind names the method
/// it was working on, and Java will leave that one method alone if asked to.
///
/// The cost is a little speed wherever that method is called and nothing anywhere else, which is
/// a trade worth making against a game that will not stay up.
///
/// The form matters and is not obvious. Java's flag wants "package.Class::method" — dots for the
/// package, two colons before the method — and refuses the two spellings anybody would try
/// instead, "package/Class::method" and "package.Class.method", with a parse error it then
/// carries on past. Which is exactly the shape Java itself prints in the error file, so the name
/// is copied through rather than rebuilt.
/// </summary>
public static partial class CompilerExclusion
{
    /// <summary>
    /// Whether a name is one Java will accept. Checked wherever one is read and again where one
    /// is used: a malformed name is not refused by the JVM, it is complained about once at
    /// startup and then ignored, which would leave the game crashing exactly as before with a
    /// setting on screen claiming otherwise.
    /// </summary>
    public static bool IsMethodName(string? method) =>
        method is { Length: > 0 } name && name.Length <= 400 && MethodNamePattern().IsMatch(name);

    /// <summary>The argument itself, for a name that has already passed <see cref="IsMethodName"/>.</summary>
    public static string Argument(string method) => $"-XX:CompileCommand=exclude,{method}";

    /// <summary>What to call it on screen: the class and the method, without the package.</summary>
    public static string Short(string method)
    {
        var at = method.IndexOf("::", StringComparison.Ordinal);
        if (at < 0) return method;

        var dot = method.LastIndexOf('.', at);

        return dot < 0 ? method : method[(dot + 1)..];
    }

    /// <summary>
    /// A package, a class, two colons, a method. Constructors are "&lt;init&gt;" and static
    /// initialisers "&lt;clinit&gt;", so the angle brackets belong; dollars are in every inner
    /// class and every lambda.
    /// </summary>
    [GeneratedRegex(@"^[\w$]+(?:\.[\w$]+)*::(?:[\w$]+|<init>|<clinit>)$")]
    private static partial Regex MethodNamePattern();
}
