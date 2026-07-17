# Yan-K Avatar Toolbox localization

Localization is split by UI area so each tool can be translated and reviewed independently:

- `Shared` contains strings used by more than one tool, including update UI.
- Each remaining folder maps directly to one Yan-K tool.
- Every folder contains the same set of language files; within a folder, every language has the same keys.
- Tool titles intentionally remain in English in every language.

The language selector discovers languages from the files in `Shared`. When adding a language, add the same-named JSON file to every folder. English is loaded first as the fallback, then the selected language overrides it.

Files are flat JSON objects. Keep placeholders such as `{0}` unchanged; standard JSON escapes such as `\n`, `\"`, and `\\` are supported.
