# Fs-Mcp

A high-performance, secure C# .NET 8 Model Context Protocol (MCP) server for filesystem operations. This service exposes a strictly sandboxed local directory to MCP-compatible AI agents, providing tools for precise file manipulation, inspection, and safe data recovery.

## Architecture & Safety

Fs-Mcp is designed with strict boundaries and data integrity in mind:
* **Root Sandboxing:** All operations are strictly jailed to the specified root directory. Symlinks and path traversals (`..`) that resolve outside the root are explicitly rejected.
* **Safe Reads:** Automatically detects binary files to prevent context window corruption. Files exceeding 8KB are blocked from full reads, forcing the use of byte-range paging.
* **Atomic Mutations:** Insertions and file creations are performed via temporary files (`.tmp`) and atomically swapped to prevent data corruption during partial writes or failures.
* **Recoverable Deletions:** By default, files are moved to a managed `.trash` directory with metadata manifests, allowing the agent to undo destructive actions.

## Prerequisites

* **.NET 8.0 SDK** or later.

## Usage

The server requires a single command-line argument defining the absolute or relative path to the allowed root directory.

```bash
# Restore dependencies and build the executable
dotnet build -c Release

# Run the MCP server targeting a specific directory
dotnet run -c Release -- /path/to/your/sandbox
```

*Note: The application communicates over `stdio`, so it is intended to be spawned as a child process by an MCP client.*

## Available MCP Tools

### Inspect & Traverse
* `file_info`: Checks if a path exists and returns its metadata (directory vs file, size, modified time, binary status, readability).
* `directory_list_files`: Non-recursively lists the contents of a directory.
* `directory_list_recursive`: Recursively lists a directory tree with pagination and optional extension filtering.

### Read Operations
* `file_read`: Reads an entire file as UTF-8 text (fails if > 8KB or binary).
* `file_range_read`: Reads a specific byte range of a UTF-8 file. Useful for paging through large logs or source files safely.

### Write Operations (Atomic & In-Place)
* `file_create`: Creates a *new* file. Fails if the file already exists.
* `file_append`: Adds text to the end of an existing file.
* `file_replace_at`: Overwrites bytes starting at a specific byte offset.
* `file_insert`: Splices text into a file at a byte offset, pushing the existing content forward without overwriting it (atomic).
* `file_truncate`: Cuts a file down to a specific byte length, discarding the remainder.

### File Management & Recovery
* `file_trash`: **(Default Delete)** Safely moves a file or directory to a hidden `.trash` directory and returns a restoration ID.
* `file_trash_restore`: Undoes a `file_trash` operation, placing the item back at its original path.
* `file_trash_restore_to`: Restores a trashed item to a new, explicit path.
* `file_delete`: Permanently and irreversibly deletes a file or directory tree.
* `file_move`: Moves or renames a file (fails if destination exists).
* `file_move_replace`: Moves or renames a file, overwriting the destination if it exists.