import * as esbuild from "esbuild";
import path from "node:path";
import { fileURLToPath } from "node:url";

const watch = process.argv.includes("--watch");
const dirname = path.dirname(fileURLToPath(import.meta.url));

const sharedLoader = {
    ".jsx": "jsx",
    // PatternFly's base.css references its own font/background assets by
    // relative url(). Fonts are emitted as separate hashed files (large,
    // inlining them would bloat app.css); the handful of small background
    // SVGs are inlined as data URLs to avoid shipping a bunch of tiny files.
    ".woff2": "file",
    ".svg": "dataurl",
};

const cockpitOptions = {
    entryPoints: ["src/app.jsx"],
    bundle: true,
    outdir: "dist",
    format: "iife",
    target: "es2020",
    logLevel: "info",
    minify: !watch,
    sourcemap: watch ? "inline" : false,
    loader: sharedLoader,
};

// Every component under src/components/ imports api.js by relative path (e.g. "../api.js") --
// this plugin redirects that one specifier to api.standalone.js for this build only, so the
// entire shared component tree bundles unmodified against the fetch()-based transport instead of
// cockpit.http(). See api.standalone.js's own comment for why the two need to have the same
// exported shape.
const standaloneApiPlugin = {
    name: "standalone-api",
    setup(build) {
        build.onResolve({ filter: /(^|\/)api\.js$/ }, () => ({
            path: path.join(dirname, "src/api.standalone.js"),
        }));
    },
};

const standaloneOptions = {
    entryPoints: ["src/standalone/app.jsx"],
    bundle: true,
    outdir: "standalone/dist",
    format: "iife",
    target: "es2020",
    logLevel: "info",
    minify: !watch,
    sourcemap: watch ? "inline" : false,
    loader: sharedLoader,
    plugins: [standaloneApiPlugin],
};

if (watch) {
    const contexts = await Promise.all([cockpitOptions, standaloneOptions].map(options => esbuild.context(options)));
    await Promise.all(contexts.map(ctx => ctx.watch()));
} else {
    await Promise.all([esbuild.build(cockpitOptions), esbuild.build(standaloneOptions)]);
}
