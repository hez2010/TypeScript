package main

import (
	"bufio"
	"encoding/base64"
	stdjson "encoding/json"
	"os"

	"github.com/microsoft/TypeScript/tsc/internal/execute/incremental"
	"github.com/microsoft/TypeScript/tsc/internal/json"
)

func main() {
	scanner := bufio.NewScanner(os.Stdin)
	scanner.Buffer(make([]byte, 65536), 32*1024*1024)
	writer := bufio.NewWriter(os.Stdout)
	defer writer.Flush()
	for scanner.Scan() {
		var request struct {
			Info stdjson.RawMessage `json:"info"`
			Text string `json:"text"`
			TextBase64 string `json:"textBase64"`
			IncludeText bool `json:"includeText"`
		}
		if err := stdjson.Unmarshal(scanner.Bytes(), &request); err != nil { panic(err) }
		var response any
		if request.Info != nil {
			var info incremental.BuildInfo
			if err := json.Unmarshal(request.Info, &info); err != nil { panic(err) }
			bytes, err := json.Marshal(&info)
			if err != nil { panic(err) }
			response = struct {
				Info stdjson.RawMessage `json:"info"`
				ValidVersion bool `json:"validVersion"`
				Incremental bool `json:"incremental"`
			}{bytes, info.IsValidVersion(), info.IsIncremental()}
		} else {
			text := request.Text
			if request.TextBase64 != "" {
				bytes, err := base64.StdEncoding.DecodeString(request.TextBase64)
				if err != nil { panic(err) }; text = string(bytes)
			}
			response = map[string]string{"hash": incremental.ComputeHash(text, request.IncludeText)}
		}
		bytes, err := json.Marshal(response)
		if err != nil { panic(err) }
		writer.Write(bytes); writer.WriteByte('\n')
	}
	if err := scanner.Err(); err != nil { panic(err) }
}
