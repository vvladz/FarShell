# Session profiles

The broker loads `config.json` from its state directory (`~/.farshell` by
default) once at startup. `--state-dir <directory>` changes that directory;
`--config <path>` selects a specific file. The client never reads this file.

If the default file is absent, the built-in `default` profile starts
`pwsh.exe -NoLogo`, inherits the broker's startup environment and working
directory, and injects the `--send` helper. An explicitly supplied missing file
is an error. Existing invalid configuration prevents the broker from listening.

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
        "FOO_BAR": null
      }
    }
  }
}
```

Adjust the example directories to existing paths. FarShell does not create
profile working directories. A minimal [example file](examples/config.json)
contains only the `home` profile.

Connect using the configured default or select a name:

```powershell
.\FarShell.Client.exe
.\FarShell.Client.exe --profile work
```

## Schema and validation

| Setting | Rule |
| --- | --- |
| `defaultProfile` | Required; names an existing profile |
| `profiles` | Required object with at least one profile |
| Profile name | 1–64 ASCII letters, digits, `.`, `_`, `-`; first character must be a letter or digit |
| `shell.file` | Required executable; absolute path or executable name on the broker's startup PATH |
| `shell.args` | Optional array of literal strings; empty arguments are supported |
| `workingDirectory` | Optional/null to inherit; otherwise an existing absolute directory after expansion |
| `env` | Optional object; string values set variables, null removes them |

Profile lookup and uniqueness use ordinal, case-insensitive comparison. Original
casing is retained. Unknown JSON properties, duplicate properties, duplicate
environment names, unresolved executables, invalid arguments, missing directories,
and invalid defaults are rejected. Environment names cannot be empty or contain
`=` or NUL; strings cannot contain NUL.

Arguments are quoted for Windows process creation individually. They are not
joined into a command script. To run a script, explicitly configure an interpreter
and its script argument. Executable resolution uses the broker's startup PATH;
profile environment changes do not participate in finding the executable.

## Environment and directory behavior

Windows `%NAME%` references in `shell.file`, `workingDirectory`, and environment
values expand once against the original broker startup environment. Unknown
references remain literal. Arguments are literal and are not expanded by FarShell.
One `env` entry cannot refer to another entry's new value, so property order has
no effect.

Each child gets a copy of the startup environment with its overrides/removals.
The broker then injects its private `FARSHELL_SEND_PIPE` and prepends the directory
containing `FarShell.Broker.exe` to the effective PATH. These helper settings stay
available even when the profile replaces or removes PATH or the pipe variable.
The launched shell may subsequently modify its own environment.

`workingDirectory` changes the child process current directory; it does not set
HOME and does not change the broker directory. Set HOME explicitly in `env` when
needed. Standalone upload/download paths remain relative to the broker's directory.

Configuration is immutable for the running broker. Restart to apply changes;
restarting ends active shells. Every paired client may select every configured
profile. An unknown name is rejected before any process or session slot is created.

Keep secrets out of this JSON: it is a normal configuration file, not a protected
secret store. Profile environment values are not printed by FarShell diagnostics,
but code running in the shell can inspect its environment.
