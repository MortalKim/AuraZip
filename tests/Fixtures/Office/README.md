# Legacy Office fixtures

These DOC/PPT samples come from NPOI's test-data collection, pinned to source commit `a0f50a01a845aa4dd793cee0ac5b776745966cb6` (NPOI 2.7.6):

https://github.com/nissl-lab/npoi/tree/a0f50a01a845aa4dd793cee0ac5b776745966cb6/testcases/test-data

Directory names preserve the upstream `document/` and `slideshow/` paths. The upstream Apache-2.0 license is included as `Apache-LICENSE.txt`.

The samples cover Word 97 text, tables, Unicode, cached field results, presentation outlines and drawing text. `SimpleMacro.doc` and `SimpleMacro.ppt` deliberately contain macros; tests feed them only to the read-only parser and never launch Office or execute those macros.
