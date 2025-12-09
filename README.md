# CSharp-PostScript Compiler

This is an experimental joke compiler that compiles C# to PostScript.

Try it: [CsPsc Playground](https://cspsc.utatane.dev/)

## Compiler specs

Supports a subset of C# language features. See [Supported language features](https://gist.github.com/tmyt/a188c94090aa9262f16a17caf40d2f0e) for details.

## PostScript interop

You can call any PostScript operators via `DllImportAttribute`.

```cs
// Represents `x y moveto`
[DllImport("flavor text here")]
extern static void MoveTo(double x, double y);
```

Method names are implicitly lowercased as PostScript operators.

## License

MIT License, except `apps/web/` which is AGPL-3.0 (due to ghostpdl-wasm dependency).
