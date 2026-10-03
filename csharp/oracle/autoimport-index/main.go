// Development differential oracle only; never invoked by the backend.
package main

import (
	"bufio"
	"crypto/sha256"
	"encoding/base64"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"github.com/microsoft/TypeScript/tsc/internal/ls/autoimport"
	"os"
	"unicode"
)

type entry struct {
	id   int
	name string
}

func (e entry) Name() string { return e.name }
func decode(s string) string {
	b, err := base64.StdEncoding.DecodeString(s)
	if err != nil {
		panic(err)
	}
	return string(b)
}
func main() {
	scanner := bufio.NewScanner(os.Stdin)
	scanner.Buffer(make([]byte, 4096), 64*1024*1024)
	writer := json.NewEncoder(os.Stdout)
	for scanner.Scan() {
		var input struct {
			Mode    string   `json:"mode"`
			Names   []string `json:"names"`
			Queries []string `json:"queries"`
			Keep    []int    `json:"keep"`
			Nil     bool     `json:"nil"`
		}
		if err := json.Unmarshal(scanner.Bytes(), &input); err != nil {
			panic(err)
		}
		if input.Mode == "unicode" {
			hash := sha256.New()
			var data [16]byte
			for c := rune(0); c <= unicode.MaxRune; c++ {
				binary.LittleEndian.PutUint32(data[:4], uint32(unicode.ToUpper(c)))
				binary.LittleEndian.PutUint32(data[4:8], uint32(unicode.ToLower(c)))
				binary.LittleEndian.PutUint32(data[8:12], uint32(unicode.SimpleFold(c)))
				data[12] = 0
				if unicode.IsUpper(c) {
					data[12] = 1
				}
				data[13] = 0
				if unicode.IsLower(c) {
					data[13] = 1
				}
				hash.Write(data[:])
			}
			writer.Encode(fmt.Sprintf("%x", hash.Sum(nil)))
			continue
		}
		idx := &autoimport.Index[entry]{}
		words := [][]int{}
		for i, name := range input.Names {
			name = decode(name)
			offsets := autoimport.CSharpWordIndices(name)
			if offsets == nil {
				offsets = []int{}
			}
			words = append(words, offsets)
			if len(name) > 0 {
				autoimport.CSharpIndexInsert(idx, entry{i, name})
			}
		}
		if input.Nil {
			idx = nil
		}
		var cloned *autoimport.Index[entry]
		if input.Keep != nil {
			cloned = idx.Clone(func(e entry) bool {
				for _, id := range input.Keep {
					if id == e.id {
						return true
					}
				}
				return false
			})
		} else {
			cloned = idx.Clone(func(e entry) bool { return true })
		}
		results := [][][][]int{}
		for _, index := range []*autoimport.Index[entry]{idx, cloned} {
			byQuery := [][][]int{}
			for _, query := range input.Queries {
				query = decode(query)
				row := [][]int{}
				if index != nil {
					for _, matches := range [][]entry{index.Find(query, true), index.Find(query, false), index.SearchWordPrefix(query)} {
						ids := []int{}
						for _, e := range matches {
							ids = append(ids, e.id)
						}
						row = append(row, ids)
					}
				}
				byQuery = append(byQuery, row)
			}
			results = append(results, byQuery)
		}
		if err := writer.Encode(map[string]any{"words": words, "results": results, "cloneNil": cloned == nil}); err != nil {
			panic(err)
		}
	}
	if scanner.Err() != nil {
		panic(fmt.Sprint(scanner.Err()))
	}
}
