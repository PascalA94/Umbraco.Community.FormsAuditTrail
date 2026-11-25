import tseslint from 'typescript-eslint';

// SEC-I2 / pitfall S3: stored strings (form names, user names, captions, values, Detail) are rendered through Lit text
// bindings only. Any HTML sink would turn one of them into script. The CI grep in build.yml is the second, cruder net.
const MESSAGE = 'HTML sinks are banned: render stored text through Lit text bindings (SEC-I2).';
const SINK_FILES = /unsafe-html|unsafe-svg|static-html/;

export default tseslint.config({
  files: ['src/**/*.{ts,tsx,js,mjs}'],
  languageOptions: { parser: tseslint.parser },
  rules: {
    'no-restricted-imports': [
      'error',
      {
        // Patterns, not exact paths, so a specifier without the .js suffix or through a different package prefix is
        // caught as well.
        patterns: [{ group: ['**/unsafe-html*', '**/unsafe-svg*', '**/static-html*'], message: MESSAGE }],
      },
    ],
    'no-restricted-syntax': [
      'error',
      {
        selector: `ImportExpression[source.value=${SINK_FILES}]`,
        message: MESSAGE,
      },
      {
        selector: 'AssignmentExpression[left.property.name=/^(innerHTML|outerHTML)$/]',
        message: MESSAGE,
      },
      {
        selector: 'CallExpression[callee.property.name=/^(insertAdjacentHTML|createContextualFragment|setHTMLUnsafe|parseHTMLUnsafe)$/]',
        message: MESSAGE,
      },
      {
        // Bracket access with a string key, which the dotted selectors above do not see.
        selector: 'MemberExpression[computed=true][property.value=/^(innerHTML|outerHTML|insertAdjacentHTML)$/]',
        message: MESSAGE,
      },
      {
        // Template-literal key without substitutions, e.g. el[`innerHTML`].
        selector:
          'MemberExpression[computed=true][property.type="TemplateLiteral"][property.expressions.length=0][property.quasis.0.value.cooked=/^(innerHTML|outerHTML|insertAdjacentHTML)$/]',
        message: MESSAGE,
      },
      {
        selector: 'CallExpression[callee.object.name="document"][callee.property.name=/^(write|writeln)$/]',
        message: MESSAGE,
      },
    ],
  },
});
