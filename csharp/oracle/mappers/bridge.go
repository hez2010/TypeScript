// Development-only access to the reference's protocol codec and identity calculation.
package contentmapper

import (
	"github.com/microsoft/TypeScript/tsc/internal/core"
	"github.com/microsoft/TypeScript/tsc/internal/json"
)

func CSharpDecodeResult(raw []byte, original, encoding, source string) (Result, error) {
	return decodeTransformResult(json.Value(raw), original, PositionEncoding(encoding), source)
}

func CSharpCombinedIdentity(mapper *Mapper, configIdentity string, options *core.CompilerOptions) string {
	return combinedIdentity(mapper, configIdentity, options)
}
