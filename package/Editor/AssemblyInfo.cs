using System.Runtime.CompilerServices;

// Lets the editor tests drive the Edit Mode preview the inspector drives, which is internal.
[assembly: InternalsVisibleTo("SpriteStudio.Editor.Tests")]
[assembly: InternalsVisibleTo("SpriteStudio.UI.Editor")]
