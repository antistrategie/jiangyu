# Available tools

These are the tools Studio exposes to AI agents via the Model Context Protocol (MCP). You don't call these directly; the agent uses them automatically when working on your requests. This page documents what the agent can do.

## Template tools

| Tool | What it does |
|---|---|
| `jiangyu_templates_search` | Search templates by name or type substring |
| `jiangyu_templates_query` | Get the full field schema for a template type (names, types, collections, enums) |
| `jiangyu_templates_inspect` | Inspect a template instance's vanilla field values |
| `jiangyu_templates_value` | Read a single field value from a template instance |
| `jiangyu_templates_enum_members` | List all members of a game enum type |
| `jiangyu_templates_parse` | Parse a KDL string and return structured AST or errors |
| `jiangyu_templates_serialise` | Serialise a template document back to KDL text |
| `jiangyu_templates_index_status` | Check whether the template index is current |
| `jiangyu_templates_index` | Rebuild the template index |
| `jiangyu_templates_project_clones` | List template clones in the project |

## Asset tools

| Tool | What it does |
|---|---|
| `jiangyu_assets_search` | Search game assets by name, type, or category |
| `jiangyu_assets_preview` | Get a preview of an asset |
| `jiangyu_assets_export` | Export an asset for editing |
| `jiangyu_assets_index_status` | Check whether the asset index is current |
| `jiangyu_assets_index` | Rebuild the asset index |

## File tools

| Tool | What it does |
|---|---|
| `jiangyu_list_directory` | List files in a project directory |
| `jiangyu_list_all_files` | List all files in the project |
| `jiangyu_create_file` | Create a new file |
| `jiangyu_edit_file` | Edit an existing file |
| `jiangyu_create_directory` | Create a directory |
| `jiangyu_move_path` | Move or rename a file or directory |
| `jiangyu_copy_path` | Copy a file or directory |
| `jiangyu_delete_path` | Delete a file or directory |
| `jiangyu_grep` | Search file contents by pattern |

## Compile tools

| Tool | What it does |
|---|---|
| `jiangyu_compile` | Compile the mod (blocks until complete, returns full result) |
| `jiangyu_compile_summary` | Get the result of the last compile |

## Config tools

| Tool | What it does |
|---|---|
| `jiangyu_config_status` | Check Jiangyu configuration (game path, editor path, cache) |
| `jiangyu_read_manifest` | Read the project's `jiangyu.json` manifest |

## Documentation tools

| Tool | What it does |
|---|---|
| `jiangyu_docs_list` | List all available reference documents |
| `jiangyu_docs_read` | Read a reference document (KDL syntax, manifest schema, etc.) |

## Context resources

In addition to callable tools, Studio provides MCP resources the agent can fetch:

- `jiangyu://project-context` — current project root, manifest content, and configuration status.
- `jiangyu://docs/{key}` — reference documentation (same content as `jiangyu_docs_read`).

These give the agent background knowledge about your project without needing to call tools.
