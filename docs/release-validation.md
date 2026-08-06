# Public Release Validation

Validation date: 2026-08-06

Release: `0.2.8-beta.1`

## Build

- .NET Release build: passed
- Compiler warnings: 0
- Compiler errors: 0
- .NET tests: 118 passed
- Plugin `npm test`: passed
- Plugin `npm run build`: passed

## Package

- Target: Windows x64
- Deployment: framework-dependent
- Required runtime: .NET 8 Desktop Runtime x64
- Package files: 35
- Uncompressed size: approximately 3.31 MB
- ZIP size: approximately 1.55 MB
- ZIP SHA256:
  `C38DC56CB533512E1FC3EDDF8EF9DA8A858ED15088BB26F95EC240161652290C`

## Content checks

The release package contains:

- Agent, Wrapper, Control Panel and required runtime dependencies;
- Vnite plugin files;
- MTool Bridge probe package;
- installation script;
- Chinese installation guide;
- license and third-party notices;
- per-file SHA256 checksums.

The release package does not contain:

- PDB files;
- TestGame binaries;
- Vnite application files;
- unpacked `node_modules`;
- internal validation or phase reports;
- databases, logs, saves, shortcuts or cache data;
- personal absolute paths;
- non-Windows SQLite native runtimes.

The package was extracted and the installation script was executed against a
temporary plugin directory. Installed plugin hashes matched the packaged files.
