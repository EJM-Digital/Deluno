import { describe, expect, it } from "vitest";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { relative, resolve } from "node:path";
import ts from "typescript";

/**
 * Two source checks found by #1331 and #1332.
 *
 * 1. A followed link or window.open sends no Authorization header, so the server answers 401. Nothing in the web app
 *    may point a plain link or window.open at an /api/ route; SignedApiLink (or authedFetch plus a blob) does it signed in.
 *    The calendar feed carries its own key and is copied as an address, never followed from the app.
 * 2. A click handler that awaits a request must handle its failure, or the person sees nothing.
 */
const SRC_ROOT = resolve(__dirname, "..");

function sourceFiles(dir: string): string[] {
  const files: string[] = [];
  for (const entry of readdirSync(dir)) {
    if (entry === "node_modules" || entry === "dist") continue;
    const full = resolve(dir, entry);
    if (statSync(full).isDirectory()) files.push(...sourceFiles(full));
    else if (/\.tsx?$/.test(entry) && !/\.test\.tsx?$/.test(entry) && !/\.d\.ts$/.test(entry)) files.push(full);
  }
  return files;
}

const parsed = new Map<string, ts.SourceFile>();
function parse(file: string) {
  const cached = parsed.get(file);
  if (cached) return cached;
  const created = parseFresh(file);
  parsed.set(file, created);
  return created;
}

function parseFresh(file: string) {
  return ts.createSourceFile(file, readFileSync(file, "utf8"), ts.ScriptTarget.Latest, true, file.endsWith("x") ? ts.ScriptKind.TSX : ts.ScriptKind.TS);
}

function textOf(node: ts.Node) {
  return node.getText();
}

describe("links to /api/ routes work signed in (#1331)", () => {
  it("no plain anchor or window.open points at an /api/ route", () => {
    const found: string[] = [];
    for (const file of sourceFiles(SRC_ROOT)) {
      const source = parse(file);
      const visit = (node: ts.Node) => {
        if (ts.isJsxAttribute(node) && node.name.getText() === "href" && node.initializer && /\/api\//.test(textOf(node.initializer))) {
          const tag = node.parent.parent;
          const name = ts.isJsxOpeningElement(tag) || ts.isJsxSelfClosingElement(tag) ? tag.tagName.getText() : "";
          found.push(`${relative(SRC_ROOT, file)}: <${name} href=${textOf(node.initializer)}>`);
        }
        if (ts.isCallExpression(node) && /^(window\.)?open$/.test(node.expression.getText()) && node.arguments[0] && /\/api\//.test(textOf(node.arguments[0]))) {
          found.push(`${relative(SRC_ROOT, file)}: ${textOf(node)}`);
        }
        if (ts.isBinaryExpression(node) && /location(\.href)?$/.test(node.left.getText()) && /\/api\//.test(textOf(node.right))) {
          found.push(`${relative(SRC_ROOT, file)}: ${textOf(node)}`);
        }
        ts.forEachChild(node, visit);
      };
      visit(source);
    }
    expect(found, "use SignedApiLink so the request carries the sign-in").toEqual([]);
  }, 60_000);
});

const HANDLER_ATTRIBUTES = new Set(["onClick", "onSubmit", "onSelect"]);

/** Helpers that never reject: they turn any failure into words. */
const NEVER_REJECTS = new Set(["describeRequestFailure", "describeAuthRefusal"]);

/**
 * True when an await sits outside any try block that has a catch clause (awaits in a catch or finally are not protected by it).
 * An await is fine when it cannot reject: `.catch(...)` chained on it, a helper that never rejects, or a function in the
 * same file that itself handles every failure (a "run" wrapper).
 */
function hasUnprotectedAwait(fn: ts.FunctionLikeDeclaration, functions: Map<string, ts.FunctionLikeDeclaration>, visiting = new Set<ts.Node>()): boolean {
  if (visiting.has(fn)) return false;
  visiting.add(fn);
  let unprotected = false;
  const cannotReject = (awaited: ts.Expression): boolean => {
    let current: ts.Expression = awaited;
    while (ts.isParenthesizedExpression(current)) current = current.expression;
    if (!ts.isCallExpression(current)) return false;
    const callee = current.expression;
    if (ts.isPropertyAccessExpression(callee) && callee.name.text === "catch") return true;
    if (ts.isIdentifier(callee)) {
      if (NEVER_REJECTS.has(callee.text)) return true;
      const target = functions.get(callee.text);
      if (target && target !== fn) return !hasUnprotectedAwait(target, functions, visiting);
    }
    return false;
  };
  const visit = (node: ts.Node, protectedByTry: boolean) => {
    if (node !== fn && ts.isFunctionLike(node)) return; // a nested function is its own handler
    if (ts.isAwaitExpression(node) && !protectedByTry && !cannotReject(node.expression)) unprotected = true;
    if (ts.isTryStatement(node)) {
      visit(node.tryBlock, protectedByTry || node.catchClause !== undefined);
      if (node.catchClause) visit(node.catchClause, protectedByTry);
      if (node.finallyBlock) visit(node.finallyBlock, protectedByTry);
      return;
    }
    ts.forEachChild(node, (child) => visit(child, protectedByTry));
  };
  if (fn.body) visit(fn.body, false);
  visiting.delete(fn);
  return unprotected;
}

function calledName(expression: ts.Expression): string | null {
  let current: ts.Node = expression;
  while (ts.isParenthesizedExpression(current) || ts.isVoidExpression(current) || ts.isAwaitExpression(current)) current = current.expression;
  if (ts.isCallExpression(current) && ts.isIdentifier(current.expression)) return current.expression.text;
  return null;
}

describe("click handlers handle a failed request (#1332)", () => {
  it("no click, submit or select handler awaits a request without a catch", () => {
    const found: string[] = [];
    for (const file of sourceFiles(SRC_ROOT)) {
      const source = parse(file);
      const functions = new Map<string, ts.FunctionLikeDeclaration>();
      const collect = (node: ts.Node) => {
        if (ts.isFunctionDeclaration(node) && node.name) functions.set(node.name.text, node);
        if (ts.isVariableDeclaration(node) && ts.isIdentifier(node.name) && node.initializer && (ts.isArrowFunction(node.initializer) || ts.isFunctionExpression(node.initializer))) {
          functions.set(node.name.text, node.initializer);
        }
        ts.forEachChild(node, collect);
      };
      collect(source);

      const report = (attribute: ts.JsxAttribute, fn: ts.FunctionLikeDeclaration, label: string) => {
        if (hasUnprotectedAwait(fn, functions)) {
          const { line } = source.getLineAndCharacterOfPosition(attribute.getStart());
          found.push(`${relative(SRC_ROOT, file)}:${line + 1} ${attribute.name.getText()} -> ${label}`);
        }
      };
      const visit = (node: ts.Node) => {
        if (ts.isJsxAttribute(node) && HANDLER_ATTRIBUTES.has(node.name.getText()) && node.initializer && ts.isJsxExpression(node.initializer) && node.initializer.expression) {
          const expression = node.initializer.expression;
          if (ts.isArrowFunction(expression) || ts.isFunctionExpression(expression)) {
            report(node, expression, "inline handler");
            // an inline handler that just calls a same-file async function: that function is the handler
            const body = ts.isBlock(expression.body) ? (expression.body.statements.length === 1 && ts.isExpressionStatement(expression.body.statements[0]) ? expression.body.statements[0].expression : null) : expression.body;
            const name = body ? calledName(body) : null;
            const target = name ? functions.get(name) : undefined;
            const awaitedHere = body !== null && ts.isAwaitExpression(body);
            if (target && !awaitedHere) report(node, target, name!);
          } else if (ts.isIdentifier(expression)) {
            const target = functions.get(expression.text);
            if (target) report(node, target, expression.text);
          }
        }
        ts.forEachChild(node, visit);
      };
      visit(source);
    }
    expect(found, "wrap the awaited request in try/catch and tell the person why it failed").toEqual([]);
  }, 60_000);
});
