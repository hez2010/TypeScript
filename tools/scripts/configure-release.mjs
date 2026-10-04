import fs from "node:fs";

const numericIdentifier = String.raw`(?:0|[1-9]\d*)`;
const releaseVersionPattern = new RegExp(
    String.raw`^(${numericIdentifier})\.(${numericIdentifier})\.(${numericIdentifier})(?:-(beta|rc))?$`,
);
const maxUint32 = 0xffff_ffffn;

const [version, expectedMajorMinor] = process.argv.slice(2);

if (!version || !isReleaseVersion(version)) {
    throw new Error(
        "Usage: node tools/scripts/configure-release.mjs <major.minor.0-beta|major.minor.1-rc|major.minor.patch (patch >= 2)> [expected-major.minor]",
    );
}

const majorMinor = version.split(".", 2).join(".");
if (expectedMajorMinor && expectedMajorMinor !== majorMinor) {
    throw new Error(`Version ${version} has major.minor ${majorMinor}, not ${expectedMajorMinor}`);
}

fs.writeFileSync("csharp/version.txt", version + "\n");

/**
 * @param {string} value
 */
function isReleaseVersion(value) {
    const match = releaseVersionPattern.exec(value);
    if (match === null || !match.slice(1, 4).every(component => BigInt(component) <= maxUint32)) {
        return false;
    }

    const patch = BigInt(match[3]);
    switch (match[4]) {
        case "beta":
            return patch === 0n;
        case "rc":
            return patch === 1n;
        default:
            return patch >= 2n;
    }
}
