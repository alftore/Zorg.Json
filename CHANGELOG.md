# Changelog

## 0.1.0-preview.2

- Added `GenerateSample`, which produces a document that conforms to a schema.
- Added Draft-04 import.
- `[DefaultValue]` on a typed property now becomes the `default` keyword, and the property is optional unless it's `required`. It's an annotation, as the attribute is: parsing doesn't fill the value in.
- Imported schemas report `additionalProperties: false` as `unrecognized_keys`, with the keys listed, like fluent schemas.
- Fixed `SafeParse` throwing when a rewrite sat next to a string with a lone surrogate.
- Packages now link to the repository.

## 0.1.0-preview.1

First preview.
