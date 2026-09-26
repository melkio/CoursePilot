## Architecture Analysis — #<issue-number>

### Module Placement
<!-- module folder name (plural); new or existing -->

### Domain Resource
<!-- Resource and ResourceId, properties, nested types -->

### Repository Contract (I<Plural>Repository)
<!-- methods to add, with signatures; mark CRUD vs custom -->

### Service Contract (I<Plural>Service)
<!-- methods and commands; one record per command, with target id + attributes -->

### Notifier Contract (I<Plural>Notifier)
<!-- events the module emits, if applicable -->

### Mongo Adapter
<!-- Document, Mapper, MongoRepository; type conversions -->

### Transports
<!-- Http endpoints (verb/route/models) and/or Amqp consumers/producers -->

### Cross-Module Interactions
<!-- list of I<Other>Service dependencies -->

### Folder Structure
<!-- exact file tree under Modules/<Plural>/ -->

### Dependency Registration
<!-- DI registration notes for the Host project -->

### Open Questions
<!-- any ambiguous points requiring clarification -->
