package main

import (
	"bufio"
	"encoding/json"
	"os"

	"github.com/microsoft/TypeScript/tsc/internal/core"
	"github.com/microsoft/TypeScript/tsc/internal/spanmap"
)

type input struct {
	Name, Virtual, Original string
	Mappings                json.RawMessage
	Start, End              int
	Feature                 spanmap.Feature
}

func main() {
	lines := bufio.NewScanner(os.Stdin)
	lines.Buffer(make([]byte, 4096), 64*1024*1024)
	writer := json.NewEncoder(os.Stdout)
	for lines.Scan() {
		var r input
		if err := json.Unmarshal(lines.Bytes(), &r); err != nil {
			panic(err)
		}
		if err := writer.Encode(process(r)); err != nil {
			panic(err)
		}
	}
	if err := lines.Err(); err != nil {
		panic(err)
	}
}

func process(r input) any {
	m, err := spanmap.Unmarshal(r.Mappings)
	if err != nil {
		return []any{"decode"}
	}
	if err := m.Validate(r.Virtual, r.Original); err != nil {
		return []any{"validation", err.Kind, err.VirtualPos, err.OriginalPos}
	}
	p, pf := m.VirtualToOriginalPosition(core.TextPos(r.Start))
	pe, exact := m.VirtualToOriginalPositionExact(core.TextPos(r.Start))
	fp, ff := m.VirtualToOriginalPositionForFeature(core.TextPos(r.Start), r.Feature)
	s, sf := m.VirtualToOriginalSpan(core.NewTextRange(r.Start, r.End))
	fs, fsf := m.VirtualToOriginalSpanForFeature(core.NewTextRange(r.Start, r.End), r.Feature)
	pos := []any{}
	for _, v := range m.OriginalToVirtualPositions(core.TextPos(r.Start), r.Feature) {
		pos = append(pos, []any{v.Position, v.Fidelity})
	}
	spans := func(values []spanmap.MappedSpan) []any {
		result := []any{}
		for _, v := range values {
			result = append(result, []any{v.Span.Pos(), v.Span.End(), v.Fidelity})
		}
		return result
	}
	raw, _ := m.Marshal()
	_, alias := m.AliasForVirtualSpan(core.NewTextRange(r.Start, r.End))
	return []any{json.RawMessage(raw), []any{p, pf}, []any{pe, exact}, []any{fp, ff}, []any{s.Pos(), s.End(), sf}, []any{fs.Pos(), fs.End(), fsf}, pos, spans(m.OriginalToVirtualSpans(core.NewTextRange(r.Start, r.End), r.Feature)), spans(m.OriginalToVirtualIntersectingSpans(core.NewTextRange(r.Start, r.End), r.Feature)), alias}
}
