# CodoMetis.EFCore.SchemaKit

The kernel of SchemaKit for EF Core 11: the host that carries schema objects EF Core has no API
for through every migration path, and the contract provider packages contribute through.

You do not reference this package by itself. Reference the package of your provider, which brings
the kernel at the version it was built against:

- [CodoMetis.EFCore.SchemaKit.PostgreSQL](https://www.nuget.org/packages/CodoMetis.EFCore.SchemaKit.PostgreSQL)

This is a preview. Any API, the contributor contract and the stored format of a declaration may
change between previews.

Documentation, the design and the issues are in the
[repository](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit).
