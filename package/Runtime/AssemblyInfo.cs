using System.Runtime.CompilerServices;

// Grants the (white-box) tests access to internal types such as UpdateManager /
// ISpriteStudioRenderable. Required to directly verify documented invariants
// (add/remove during tick, zero allocation, domain-reload reset, etc.). No public key
// is needed because Unity assemblies are not strong-named by default.
[assembly: InternalsVisibleTo("SpriteStudio.Runtime.Tests")]
[assembly: InternalsVisibleTo("SpriteStudio.Runtime.EditModeTests")]
