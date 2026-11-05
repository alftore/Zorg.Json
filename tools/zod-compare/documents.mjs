// The shared documents. Each is JSON text, so Zorg sees exactly what is written here (`1.0`, `1e2`, `-0`,
// duplicate keys) while Zod sees JSON.parse of it. Random ones come from a fixed seed, so the fixture is
// byte-for-byte reproducible.

const SEED = 20261105;

function mulberry32(seed) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const raw = (tags, ...texts) => texts.map((text) => ({ text, tags }));
const str = (tags, ...values) => values.map((v) => ({ text: JSON.stringify(v), tags }));

function hand() {
  return [
    ...raw(["null"], "null"),
    ...raw(["bool"], "true", "false"),
    ...raw(
      ["number", "numeric-edge"],
      "0", "-0", "0.0", "-0.0", "1", "-1", "1.0", "2.0", "4.0", "1.5", "-1.5", "1e2", "1E2", "1e-2", "1.0e0", "0e0", "0.1e1", "0.1", "0.2",
      "0.3", "0.30000000000000004", "0.5", "3", "5", "7", "9", "10", "10.5", "11", "15", "42", "100", "101", "255", "256",
      "5e-324", "1e-400", "100000000000000000000",
    ),
    ...raw(
      ["number", "int-edge"],
      "2147483647", "2147483648", "-2147483648", "-2147483649", "4294967296",
    ),
    ...raw(
      ["number", "bigint"],
      "9007199254740991", "9007199254740992", "9007199254740993", "-9007199254740991", "-9007199254740993",
      "9223372036854775807", "9223372036854775808", "12345678901234567890", "123456789012345678901234567890",
    ),
    ...raw(["number", "large-double"], "1e308", "1.7976931348623157e308"),
    ...raw(["number"], "1e400", "-1e400"),
    ...str(["string", "empty"], ""),
    ...str(["string"], " ", "a", "ab", "abc", "abcd", "abcde", "abcdef", "ABC", "Abc", "abc123", "123", "ab-cd", "a-b-c", "a/b", "web", "WEB", "News", "red", "green", "blue", "Red"),
    ...str(["string", "whitespace"], "  abc  ", "\t\nabc\r\n", "   ", "\u0085x", "\ufeffx", "\u00a0x", "\u200bx", "\u2028x\u2029"),
    ...str(["string", "unicode"], "\u0663\u0663\u0663", "\u00e9", "e\u0301", "\u00df", "\u0130", "\u01c5", "\u00c5", "A\u030a", "\u212b", "\ufb01", "\u00e9\u00e9\u00e9", "caf\u00e9"),
    ...str(["string", "astral"], "\u{1F600}", "\u{1F600}\u{1F600}", "a\u{1F600}b", "\u{1F600}\u{1F600}\u{1F600}", "\u{1F468}\u200d\u{1F469}\u200d\u{1F467}", "\u{10000}", "ab\u{1F600}"),
    ...raw(["string", "lone-surrogate"], '"\\ud800"', '"\\udc00"', '"a\\ud800b"', '"\\ud83d"'),
    ...raw(["string", "control"], '"\\u0000"', '"\\u001f"', '"a\\u0000b"'),
    ...str(["string", "coerce-source"], "true", "false", "TRUE", "yes", "on", "0", "1", "42", " 42", "42 ", "4 2", "1.5", "1e2", "0x10", "NaN", "Infinity", "-0", "null", "1.0", "0b11", "1_000", "+5", ".5", "5."),
    // formats
    ...str(
      ["string", "format", "email"],
      "a@b.co", "user@example.com", "a@b", "a@b.c", "user+tag@example.co.uk", "\u00fc@example.com", "user@ex\u00e4mple.com", "a b@example.com",
      "@example.com", "user@", "a..b@example.com", ".a@example.com", "a.@example.com", "a@-b.com", "a@b..com", '"quoted"@example.com', "a@[127.0.0.1]", "user@localhost",
      "a@b@c.com", "A@B.COM", "x".repeat(65) + "@example.com",
    ),
    ...str(
      ["string", "format", "uuid"],
      "2eb8aa08-aa98-41ea-b4aa-73b441d16380", "2EB8AA08-AA98-41EA-B4AA-73B441D16380", "c232ab00-9414-11ec-b3c8-9f6bdeced846", "018f5e3a-7c3b-7c3b-8c3b-7c3b7c3b7c3b",
      "018f5e3a-7c3b-8c3b-8c3b-7c3b7c3b7c3b", "2eb8aa08-aa98-91ea-b4aa-73b441d16380", "2eb8aa08-aa98-41ea-c4aa-73b441d16380", "00000000-0000-0000-0000-000000000000",
      "ffffffff-ffff-ffff-ffff-ffffffffffff", "2eb8aa08aa9841eab4aa73b441d16380", "{2eb8aa08-aa98-41ea-b4aa-73b441d16380}", "2eb8aa08-aa98-41ea-b4aa-73b441d1638",
      "2eb8aa08-aa98-41ea-b4aa-73b441d16380 ", "urn:uuid:2eb8aa08-aa98-41ea-b4aa-73b441d16380", "2eb8aa08-aa98-41ea-b4aa-73b441d1638g",
      "12345678-1234-1234-1234-123456789012",
    ),
    ...str(
      ["string", "format", "url"],
      "http://example.com", "https://example.com/path?x=1#f", "ftp://example.com", "example.com", "http://", "http://a b", "mailto:a@b.c", "foo:bar",
      "//example.com", "/relative", "http://[::1]/", "http://127.0.0.1:80", "HTTP://EXAMPLE.COM", "http://ex\u00e4mple.com", "javascript:alert(1)", "file:///c:/x",
      "urn:isbn:123", "http://localhost", "http://a", "https://example.com:99999", "http://exa mple.com", "http://example.com/\u00e9", "http://user:pw@example.com",
      " http://example.com", "http://example.com ", "http:example.com", "a:b",
    ),
    ...str(
      ["string", "format", "datetime"],
      "2026-11-05T10:00:00Z", "2026-11-05T10:00:00.123Z", "2026-11-05T10:00:00+01:00", "2026-11-05T10:00Z", "2026-11-05T10:00:00", "2026-11-05 10:00:00Z",
      "2026-02-29T00:00:00Z", "2024-02-29T00:00:00Z", "2026-13-01T00:00:00Z", "2026-11-05T24:00:00Z", "2026-11-05T23:59:60Z", "2026-11-05t10:00:00z",
      "2026-11-05T10:00:00.Z", "2026-11-05T10:00:00-00:00", "2026-11-05T10:00:00+0100", "2026-11-05T10:00:00.123456789Z", "0000-01-01T00:00:00Z", "2026-11-05",
    ),
    ...str(
      ["string", "format", "date"],
      "2026-11-05", "2026-02-29", "2024-02-29", "2026-2-5", "20261105", "2026-11-31", "0000-01-01", "9999-12-31", "2026-11-05 ", "2026-00-10", "2026-11-00",
      "-2026-11-05", "+2026-11-05",
    ),
    ...str(["string", "format", "time"], "10:00:00", "10:00", "10:00:00Z", "10:00:00.5", "24:00:00", "23:59:60", "10:00:00+01:00", "1:00:00", "10:60:00"),
    ...str(
      ["string", "format", "ipv4"],
      "127.0.0.1", "0.0.0.0", "255.255.255.255", "256.0.0.1", "1.2.3", "1.2.3.4.5", "01.2.3.4", "1.2.3.04", "1.2.3.4 ", "\u0663.\u0663.\u0663.\u0663", "1.2.3.-4", "+1.2.3.4",
    ),
    ...str(
      ["string", "format", "ipv6"],
      "::1", "::", "2001:db8::1", "1:2:3:4:5:6:7:8", "1:2:3:4:5:6:7", "::ffff:1.2.3.4", "12345::", "fe80::1%eth0", "1::2::3", "[::1]", "::g", "1:2:3:4:5:6:7:8:9",
      "2001:DB8::1", "::1.2.3.4", "1:2:3:4:5:6:1.2.3.4",
    ),
    ...str(
      ["string", "format", "hostname"],
      "example.com", "localhost", "a-b.example", "-a.com", "a-.com", "a..b", "exa mple.com", "xn--nxasmq6b", "a".repeat(63) + ".com", "a".repeat(64) + ".com",
      "example.com.", "5.", "_dmarc.example.com", "1.2.3.4", "EXAMPLE.COM", "ex\u00e4mple.com", "a".repeat(250) + ".com",
    ),
    // arrays
    ...raw(
      ["array"],
      "[]", "[null]", "[1]", "[1,2,3]", "[1,2,3,4]", '["a"]', '["a","a"]', '["a","b"]', '[1,"a"]', '["a",1]', '["a",1,true]', '["a",1,true,null]', '["a",1,true,false]', '["a",1.5]',
      '["a",1e2]', '["a",1.0]', "[[]]", "[[1],[2,3]]", '[[1],["a"]]', "[1,1.0]", "[1.0,1]", '[{"a":1},{"a":1}]', '[{"a":1},{"a":"x"}]', '[{"a":1,"b":2},{"b":2,"a":1}]',
      '["a","b","c"]', '["a",null]', '[[[]]]', '[[1,2],[3,[4]]]', '[0,[1,[2,[3]]]]', '[0,["a"]]', '[1,[]]',
    ),
    // objects
    ...raw(
      ["object"],
      "{}", '{"name":"x","age":1}', '{"name":"x"}', '{"age":1}', '{"name":"x","age":1,"extra":true}', '{"name":null,"age":1}', '{"name":"x","age":null}',
      '{"name":"x","age":1.5}', '{"name":"x","age":1.0}', '{"name":"x","age":1e2}', '{"name":"x","age":"1"}', '{"name":1,"age":1}', '{"a":1}', '{"a":"x","b":1}', '{"b":"y"}',
      '{"a":1,"b":"y"}', '{"a":1,"b":2}', '{"a":"x","b":"y"}', '{"a":true}', '{"a":"x","b":1,"c":true}', '{"a":"x","b":1,"c":true,"d":0}', '{"a":"x","c":true}', '{"a":"x","b":1}',
      '{"a":{"b":{"c":1}}}', '{"a":{"b":{}}}', '{"a":{"b":{"c":"x"}}}', '{"a":{"b":{"c":1,"d":2}}}', '{"x":1,"y":2}', '{"x":"a"}', '{"x":1}', '{"A":1}', '{"ab":1,"CD":2}',
      '{"ab":1,"cd":2}', '{"n":null}', '{"n":5}', '{"n":"5"}', '{"n":1.5}', '{"s":null}', '{"s":"y"}', '{"s":1}',
      '{"id":1}', '{"id":1,"x":"s","y":"t"}', '{"id":1,"x":1}', '{"id":"1"}', '{"x":"s"}',
      '{"a/b":"x","c~d":"y"}', '{"a/b":1,"c~d":"y"}', '{"a/b":"x"}', '{"\\ud83d\\ude00":1}', '{"\\ud83d\\ude00":"x"}', '{"":1}', '{"":"x"}',
      '{"min":1,"max":2}', '{"min":3,"max":2}', '{"min":2,"max":2}', '{"min":"a","max":2}',
      '{"kind":"circle","radius":1}', '{"kind":"square","side":2}', '{"kind":"triangle","a":1,"b":2}', '{"kind":"square","radius":1}', '{"kind":"triangle"}',
      '{"kind":"hexagon"}', '{"kind":1}', '{"kind":null}', '{"radius":1}', '{"kind":"circle","radius":"x"}', '{"kind":"circle","radius":1,"extra":1}',
      '{"value":1,"children":[]}', '{"value":1,"children":[{"value":2,"children":[]}]}', '{"value":1,"children":[{"value":"x","children":[]}]}', '{"value":1}',
      '{"value":1,"children":[{"value":2,"children":[{"value":3,"children":[]}]}]}',
      '{"a":1,"a":2}', '{"a":"x","a":1}', '{"__proto__":{"x":1}}', '{"__proto__":1}', '{"constructor":1}', '{"toString":1}',
      '{"A":"x","B":"y"}', '{"foo":1,"bar":2}', '{"foo":1,"Bar":2}', '{"foo":"x"}', '{"0":1}',
    ),
  ];
}

function randomDocs() {
  const rand = mulberry32(SEED);
  const pick = (items) => items[Math.floor(rand() * items.length)];
  const alphabets = [
    "abcdefghij",
    "ABCXYZ",
    "0123456789",
    " \t-_/.@",
    "\u00e9\u00fc\u00f1\u00df",
    "\u{1F600}\u{1F4A9}\u{10000}",
    "\u4e2d\u6587",
  ];
  const randomString = () => {
    const length = Math.floor(rand() * 9);
    let out = "";
    for (let i = 0; i < length; i++) {
      out += [...pick(alphabets)][Math.floor(rand() * 4)] ?? "x";
    }
    return out;
  };
  const randomScalar = () => {
    const kind = Math.floor(rand() * 5);
    if (kind === 0) return Math.floor((rand() - 0.5) * 2000);
    if (kind === 1) return Math.round((rand() - 0.5) * 20000) / 100;
    if (kind === 2) return randomString();
    if (kind === 3) return rand() < 0.5;
    return null;
  };
  const keys = ["a", "b", "name", "age", "x", "y", "id", "n", "kind", "value"];
  const docs = [];
  for (let i = 0; i < 40; i++) {
    const magnitude = 10 ** Math.floor(rand() * 17);
    docs.push({ text: String(Math.floor((rand() - 0.5) * 2 * magnitude)), tags: ["number", "random"] });
  }
  for (let i = 0; i < 30; i++) {
    docs.push({ text: JSON.stringify((rand() - 0.5) * 10 ** Math.floor(rand() * 8)), tags: ["number", "random"] });
  }
  for (let i = 0; i < 40; i++) {
    docs.push({ text: JSON.stringify(randomString()), tags: ["string", "random"] });
  }
  for (let i = 0; i < 25; i++) {
    const length = Math.floor(rand() * 5);
    docs.push({ text: JSON.stringify(Array.from({ length }, randomScalar)), tags: ["array", "random"] });
  }
  for (let i = 0; i < 35; i++) {
    const length = Math.floor(rand() * 4);
    const value = {};
    for (let j = 0; j < length; j++) value[pick(keys)] = randomScalar();
    docs.push({ text: JSON.stringify(value), tags: ["object", "random"] });
  }
  return docs;
}

function hasDuplicateKey(text) {
  // JSON.parse keeps the last value, so look at the text: a key repeated inside one object.
  return /"([^"\\]*)"\s*:[^{}]*,\s*"\1"\s*:/.test(text);
}

function strings(value, out = []) {
  if (typeof value === "string") out.push(value);
  else if (Array.isArray(value)) value.forEach((item) => strings(item, out));
  else if (value !== null && typeof value === "object") {
    for (const key of Object.keys(value)) {
      out.push(key);
      strings(value[key], out);
    }
  }
  return out;
}

// Tags that rules in known-differences.json can match on, derived from the text so random documents get them too.
function autoTags(text) {
  const tags = new Set();
  const value = JSON.parse(text);
  for (const s of strings(value)) {
    if (/[\ud800-\udbff](?![\udc00-\udfff])|(?<![\ud800-\udbff])[\udc00-\udfff]/.test(s)) tags.add("lone-surrogate");
    else if (/[\ud800-\udbff][\udc00-\udfff]/.test(s)) tags.add("astral");
  }
  // Number tokens anywhere in the document, with the strings blanked out first.
  const tokens = text.replace(/"(?:[^"\\]|\\.)*"/g, '""').match(/-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?/g) ?? [];
  for (const token of tokens) {
    const n = Number(token);
    const isIntegerText = /^-?\d+$/.test(token);
    if (isIntegerText && Math.abs(n) > Number.MAX_SAFE_INTEGER) tags.add("bigint");
    if (!isIntegerText && Number.isInteger(n)) tags.add("integer-valued-float");
    if (!Number.isFinite(n)) tags.add("overflow");
    if (n === 0 && /[1-9]/.test(token.split(/[eE]/)[0])) tags.add("underflow");
    if (token.startsWith("-") && n === 0) tags.add("negative-zero");
  }
  if (typeof value === "object" && value !== null && hasDuplicateKey(text)) tags.add("duplicate-key");
  if (typeof value === "object" && value !== null && /"__proto__"\s*:/.test(text)) tags.add("proto-key");
  return [...tags];
}

export function documents() {
  const byText = new Map();
  for (const doc of [...hand(), ...randomDocs()]) {
    const known = byText.get(doc.text);
    if (known) known.tags.push(...doc.tags);
    else byText.set(doc.text, { text: doc.text, tags: [...doc.tags] });
  }
  return [...byText.values()].map((doc) => ({ text: doc.text, tags: [...new Set([...doc.tags, ...autoTags(doc.text)])] }));
}
