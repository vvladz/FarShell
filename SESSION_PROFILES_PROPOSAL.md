# Session Profiles Proposal

## Status

This document records the proposed session-profile configuration for FarShell.
It is an implementation plan, not the current behavior.

The proposal does not change the existing shell lifecycle: each shell belongs
to one live client connection and ends on disconnect, normal exit, or broker
shutdown. Multiple clients can run independent shells concurrently.

## Goal

Allow the broker administrator to define named ways to start shell sessions,
including:

- the initial working directory;
- the shell executable and its arguments;
- environment-variable overrides;
- one broker-selected default profile.

A client selects only a profile name. It never supplies an executable, working
directory, or environment values over the network.

## Terminology

A **profile** is an immutable broker-side launch template such as `home` or
`work`. A **session** is one running ConPTY and process tree created from a
profile.

Several concurrent shells may use the same profile. Internal session GUIDs
remain protocol correlation IDs, not names or handles for reconnecting.

The term `workingDirectory` is used instead of `home` because the setting
controls the process current directory only. A profile that must also set the
`HOME` environment variable does so explicitly through `env`.

## Configuration file

The default broker configuration path is:

```text
%LOCALAPPDATA%\FarShell\config.json
```

The broker accepts `--config <path>` to select another file. The client does
not read this file.

If the default file is absent and `--config` was not supplied, the broker uses
one built-in profile that exactly preserves the current behavior:

```text
name:             default
workingDirectory: inherited from the broker
shell:            pwsh.exe -NoLogo
environment:      inherited from the broker
```

An explicitly selected configuration path must exist. If a configuration file
exists but is invalid, the broker fails before opening its listening socket.
It must not silently fall back to the built-in profile.

The broker loads the file once during startup. Hot reload is not part of the
initial implementation; changing the file requires a broker restart and
therefore ends existing sessions under the established lifecycle rules.

## Proposed schema

```json
{
  "defaultProfile": "home",
  "profiles": {
    "home": {
      "workingDirectory": "%USERPROFILE%",
      "shell": {
        "file": "pwsh.exe",
        "args": ["-NoLogo"]
      },
      "env": {}
    },
    "work": {
      "workingDirectory": "%USERPROFILE%\\Projects\\Work",
      "shell": {
        "file": "pwsh.exe",
        "args": ["-NoLogo"]
      },
      "env": {
        "FARSHELL_PROFILE": "work",
        "REMOVE_FROM_CHILD": null
      }
    }
  }
}
```

`defaultProfile` names the profile used when the client does not select one.
Keeping the default in one top-level field avoids conflicting per-profile
`isDefault` flags.

Profile names:

- are compared case-insensitively using ordinal comparison;
- preserve their configured casing for display;
- contain 1 to 64 ASCII letters, digits, dots, underscores, or hyphens;
- must start with a letter or digit;
- must be unique under the case-insensitive comparison.

The default profile must exist. At least one profile is required.

## Working-directory behavior

`workingDirectory` is optional. When omitted or `null`, the shell inherits the
broker's current directory, preserving the current behavior.

When configured, the broker expands Windows `%NAME%` references once using the
broker's startup environment. The resulting path must be absolute and must
refer to an existing directory. Validation happens at broker startup so an
invalid configured profile cannot fail unpredictably after the broker begins
accepting clients.

The broker does not create the directory and does not change its own current
directory.

## Shell behavior

`shell.file` is required and non-empty. It may be an absolute executable path
or an executable name resolvable through the broker's startup `PATH`.
`%NAME%` references are expanded once before resolution.

`shell.args` is an optional array of literal argument values. Arguments are
converted to a Windows command line with the standard Windows quoting rules;
they are not joined by simple string concatenation and are not interpreted as
a command script by the broker.

The profile environment does not participate in locating `shell.file`. A
profile that uses a shell outside the broker's startup `PATH` must specify its
absolute path. This keeps executable resolution deterministic during startup
validation.

No client-provided command, argument, or executable path is accepted. Arbitrary
command execution remains possible inside the attached shell, as it is today,
but the launch template itself is controlled locally by the broker
administrator.

## Environment behavior

The child environment starts as a case-insensitive copy of the broker's startup
environment. Each `env` entry then modifies that copy:

- a string sets or replaces the variable;
- `null` removes the variable;
- an omitted `env` object makes no changes.

String values expand Windows `%NAME%` references once against the original
broker startup environment. Entries do not reference values defined by other
entries in the same profile, so JSON property order has no effect.

Configured variable names must be non-empty and must not contain `=` or a NUL
character. Values must not contain a NUL character. Matching is
case-insensitive, consistent with Windows environment semantics.

The effective environment is encoded as a correctly sorted, double-NUL-
terminated Unicode environment block and passed to `CreateProcessW`. The block
must remain allocated until `CreateProcessW` returns and must be released on
both success and failure. Existing inherited Windows environment entries are
preserved unless the profile explicitly changes or removes them.

## Client behavior

The existing command with no operation continues to create a new shell using
the broker's default profile:

```text
FarShell.Client.exe [host] [port]
```

A client selects a named profile explicitly with:

```text
FarShell.Client.exe --profile <profile> [host] [port]
```

There is no attach or shell-listing command. A separate list-profiles operation
is not required initially; configuration distribution and profile discovery
remain operational concerns outside the terminal protocol.

## Broker model and boundaries

The broker loads and validates configuration into an immutable profile
registry. Profile lookup happens after protocol negotiation and authentication,
but before reserving a session slot or creating a ConPTY.

```text
Requested profile name or empty default selection
    -> broker profile registry
    -> resolved SessionLaunchProfile
    -> SessionManager
    -> ShellSession
    -> ConPtySession
    -> CreateProcessW
```

An unknown profile is rejected without creating a ConPTY, registering a
session, or consuming a session slot. Error messages may contain the requested
profile name but must not include environment values or other profile details.

`ShellSession` passes the immutable launch settings to the ConPTY layer.
`SessionManager` remains responsible for active-shell limits and cleanup; it
does not parse JSON or resolve paths.

The ConPTY project receives generic process-launch settings. It must not depend
on broker configuration types or know about profile names.

## Protocol boundary

The create-session payload currently contains only terminal dimensions. Adding
a profile selection is a protocol-breaking change, so this proposal requires
protocol version 3.

Version 3 adds `CreateSessionRequest`, containing terminal size and an optional
profile name.

An empty profile name in `CreateSessionRequest` means "use the broker default."

Profile text is strict UTF-8 with explicit length prefixes and the same
1-to-64-character validation used by the broker registry. Malformed lengths,
invalid UTF-8, and invalid names are protocol errors. The overall frame limit
remains unchanged.

Version 2 clients and version 3 brokers reject each other through the existing
version negotiation. Broker and client must therefore be deployed together,
consistent with the existing protocol upgrade policy.

## Relationship to identity and authorization

Profiles are independent of the proposed single API key and TLS mechanism in
`AUTH_PROPOSAL.md`. Every authenticated client can select any configured
profile. Profile resolution only decides how a create operation starts its
process.

> [!WARNING]
> Do not place secrets in profile environment values. The current transport is
> unauthenticated and unencrypted. Secret injection requires secure transport,
> authentication, and a separate design for protecting those values locally.

## Validation and error handling

Configuration validation is complete before the broker listens. It covers:

- JSON syntax and unknown or duplicate properties;
- presence and uniqueness of profiles;
- existence of the default profile;
- profile-name rules;
- executable resolution;
- argument validity;
- working-directory expansion and existence;
- environment name and value validity;
- the maximum serialized profile-name size required by the protocol.

Unknown JSON properties are rejected so misspelled security- or
execution-relevant settings cannot be silently ignored.

Runtime process-creation errors produce the existing generic session-operation
failure for the client and a broker-side diagnostic that identifies the
profile and the operating-system error. Diagnostics must not print the complete
environment or values from it.

## Compatibility and rollout

The no-file built-in profile preserves current shell, directory, environment,
session-limit, disconnect, resize, exit, and shutdown behavior.

The protocol version bump is intentionally breaking. Release artifacts must
publish broker and client binaries from the same commit, and upgrade guidance
must say to replace both together. No session or configuration data migration
is required because sessions do not survive broker restart and no prior profile
configuration exists.

## Implementation sequence

1. Add immutable configuration models, JSON loading, default-path resolution,
   validation, and focused unit tests without changing session creation.
2. Add generic ConPTY process-launch settings, Windows argument quoting, and a
   Unicode environment block with tests for inheritance, override, removal,
   quoting, and cleanup on failure.
3. Define the protocol version 3 create payload with round-trip and
   malformed-payload tests.
4. Resolve profiles in the broker before session-slot acquisition, pass launch
   settings through `SessionManager` and `ShellSession`.
5. Add `--profile` and extend integration tests to cover the default profile
   and a selected profile.
6. Update `README.md` with the configuration location, schema, CLI examples,
   restart requirement, security warning, and coordinated upgrade requirement.
7. Run the Release build and the complete test suite, then manually verify a
   configured working directory, environment override, disconnect, normal shell
   exit, and broker shutdown cleanup.

## Acceptance criteria

- With no configuration file, creating a session behaves exactly as it does
  before the change.
- A valid configuration is loaded before the broker listens.
- Invalid JSON, an invalid default, duplicate profile names, an unresolved
  executable, or a missing working directory prevents broker startup with a
  useful error.
- A client with no profile selection creates a session from the configured
  default profile.
- `--profile <name>` creates a session from that profile.
- An unknown profile creates no process, registry entry, or leaked session
  slot.
- The shell starts in the configured working directory.
- The shell receives inherited variables plus configured overrides and does
  not receive explicitly removed variables.
- Shell paths and arguments containing spaces and quotes are passed correctly.
- Disconnect, resize, normal exit, session limits, and broker shutdown retain
  their existing behavior.
- Version 2 and version 3 peers fail explicitly during negotiation rather than
  misinterpreting payloads.
- Broker and client logs never print profile environment values.
