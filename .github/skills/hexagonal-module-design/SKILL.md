---
name: hexagonal-module-design
description: "Codebase-specific design conventions for Hexagonal Architecture modules in this .NET solution: Modules folder layout, Icaro `Resource<T>` pattern, repository/service/notifier triad, MongoDB persistence adapters (Document + Mapper + Repository), HTTP/AMQP transport adapters, CRUD operations, and command pattern for behaviour. Invoke this skill whenever an agent needs to design or implement a new module, or extend an existing one. Keywords: module, hexagonal, vertical slice, Icaro, Resource, ResourceId, IRepository, IService, INotifier, MongoDB document, document mapper, MongoRepository, CRUD, command, Execute, transport, Http, Amqp."
argument-hint: "Provide the module name (plural) and the scope of the change (new module, new operation, new adapter, etc.)."
user-invocable: false
---

# Hexagonal Module Design

Codebase-specific conventions for designing and implementing modules in this solution. Use this skill to decide folder layout, naming, contracts, and adapter boundaries. The skill describes **what** to design and **how** to name it — not workflow.

Reference packages: https://github.com/codiceplastico/icaro (`Icaro.Domain`, `Icaro.MongoDB`, etc.).

## 1. Where Modules Live

All modules live under the Host project:

```
src/
  <Solution>.Host/
    Gateways/
    Infrastructure/
    Modules/
      <ModuleNamePlural>/          ← one folder per module, plural name
```

Test projects mirror this layout with the `.Tests` suffix under `test/`.

## 2. Module Layout

A module groups its domain at the centre and its technological adapters in dedicated sub-folders. Example for a module named `Requests`:

```
Modules/
  Requests/
    Mongo/
      RequestDocument.cs
      RequestDocumentMapper.cs
      MongoRequestsRepository.cs
    Transports/
      Http/
        RequestsController.cs
        GetRequestModel.cs
        PostRequestModel.cs
        PutRequestModel.cs
      Amqp/
    IRequestsNotifier.cs
    IRequestsRepository.cs
    IRequestsService.cs
    Request.cs
```

Rules:
- **Domain files sit at the module root**: the resource type (`Request.cs`) and the module-level interfaces (`IRequestsRepository`, `IRequestsService`, `IRequestsNotifier`).
- **Each adapter technology has its own sub-folder** (`Mongo/`, `Transports/Http/`, `Transports/Amqp/`, …). Adapters implement the interfaces declared at the module root.
- **One component per file.**
- **No direct dependency between two modules.** If module `A` needs module `B`, it depends on `IBService` only.

## 3. Naming Conventions

- Module folder: **plural** (e.g., `Requests`).
- Resource type and resource-id type: **singular** (e.g., `Request`, `RequestId`).
- Interfaces: `I<ModulePlural><Role>` → `IRequestsRepository`, `IRequestsService`, `IRequestsNotifier`.
- Mongo types: `<ResourceSingular>Document`, `<ResourceSingular>DocumentMapper`, `Mongo<ModulePlural>Repository`.

## 4. Domain Components (Icaro)

1. Install the `Icaro.Domain` NuGet package (if not already present).
2. Create the module folder (plural) under `Modules/`, if not already present.
3. Create `<ResourceSingular>.cs` at the module root containing:
   - `<ResourceSingular>Id`: derives from `Icaro.ResourceId`.
   - `<ResourceSingular>`: derives from `Icaro.Resource<TId>` with the required properties.
4. Create `I<ModulePlural>Repository.cs` exposing at minimum:
   - `Task<(ResourceContext, <ResourceSingular>)> Get(<ResourceSingular>Id id);`
   - `Task<ResourceContext> Upsert(<ResourceSingular> resource, int? version = null);`
5. Create `I<ModulePlural>Service.cs`. This is the **only entry point** other modules may use. Start with:
   - `Task<(ResourceContext, <ResourceSingular>)> Get(<ResourceSingular>Id id);`
6. In the same file, add the concrete `<ModulePlural>Service` implementing `I<ModulePlural>Service`. The constructor receives `I<ModulePlural>Repository`. Implement `Get` by delegating to the repository.

## 5. MongoDB Persistence Adapter

1. Install `Icaro.MongoDB` (if not already present).
2. Create the `Mongo/` sub-folder inside the module.
3. `<ResourceSingular>Document.cs` — mirrors the domain resource properties **except `Id`**. Nested domain classes become nested document classes. Type conversions:
   - `Guid` (domain) → `string` (document).
   - `enum` (domain) → `string` (document).
   - `DateTime` / `DateTimeOffset` (domain) → `DateTimeDocument` (document).
4. `<ResourceSingular>DocumentMapper.cs` — implements `IDocumentMapper<<ResourceSingular>Id, <ResourceSingular>, <ResourceSingular>Document>` (namespace `Icaro`). Maps both directions.
5. `Mongo<ModulePlural>Repository.cs` — derives from `DefaultMongoRepository<<ResourceSingular>Id, <ResourceSingular>, <ResourceSingular>Document>` (namespace `Icaro`) and implements `I<ModulePlural>Repository`. Constructor takes `IMongoDatabase` and `IDocumentMapper<...>`. The base class provides `Get`, `GetAll`, `Upsert`, and `Delete` out of the box.

## 6. CRUD Operations

If the operation is plain CRUD, add the corresponding method to `I<ModulePlural>Repository`:

| Operation | Method signature |
|-----------|------------------|
| Read by id | `Task<(ResourceContext, TResource)> Get(TResourceId id)` |
| Read all | `Task<(ResourcesContext, ResourceEnvelope<TResource>[])> GetAll()` |
| Create / Update | `Task<ResourceContext> Upsert(TResource resource, int? version = null)` |
| Delete | `Task<ResourceContext> Delete(TResourceId id, int? version = null)` |

`DefaultMongoRepository` already implements all four; no extra code in `Mongo<ModulePlural>Repository`.

## 7. Behaviour — Command Pattern

If the module must express business behaviour (not plain CRUD):

1. **Define the command** as a `record` with an **imperative name** (e.g., `MarkRequestAsFailedCommand`). Place it in the same file as the service interface. The record carries the target resource id plus all attributes needed to execute it.
2. **Expose execution** on the service interface:
   - `Task Execute(MarkRequestAsFailedCommand command);`
3. **Implement** the command in the concrete service (`<ModulePlural>Service`).

One command = one explicit method. Do not bundle multiple behaviours behind generic verbs.

## 8. Cross-Cutting Rules

- Domain files at the module root must not reference any adapter technology (no `Mongo`, no HTTP).
- Adapters must not reference adapters from other modules.
- Inter-module calls always go through `I<OtherModulePlural>Service`.
- New transports (HTTP controllers, AMQP consumers) live under `Transports/<Technology>/` inside the module.
