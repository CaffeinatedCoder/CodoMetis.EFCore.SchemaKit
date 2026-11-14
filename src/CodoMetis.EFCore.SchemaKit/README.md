# CodoMetis.EFCore.SchemaKit

The kernel of SchemaKit for EF Core 11: the host that carries schema objects EF Core has no API
for through every migration path, and the contract provider packages contribute through.

You do not reference this package by itself. Reference the package of your provider, which brings
the kernel at the version it was built against:

- [CodoMetis.EFCore.SchemaKit.PostgreSQL](https://www.nuget.org/packages/CodoMetis.EFCore.SchemaKit.PostgreSQL)

This is a preview. Any API may change between previews, and so may the contributor contract, which
is marked experimental (`SK9001`) until `rc.1`, and the stored form of a declaration in the model
snapshot: a later preview may ask for the migrations to be scaffolded again. The permanent promise
starts at `11.0.0`.

Documentation, the design and the issues are in the
[repository](https://github.com/CaffeinatedCoder/CodoMetis.EFCore.SchemaKit).
