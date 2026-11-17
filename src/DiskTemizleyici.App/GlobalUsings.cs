// WPF projects leave System.IO out of the implicit usings (it clashes with System.Windows.Shapes.Path in XAML code).
global using System.IO;
global using Path = System.IO.Path;
