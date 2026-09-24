// Runs the pinned test mapper fixtures over stdio. Compilation and checking
// remain in the candidate; this process only supplies external mapper inputs.
package main

import (
	"fmt"
	"io"
	"os"

	"github.com/microsoft/TypeScript/tsc/internal/testutil/contentmappertest"
)

func main() {
	connection, err := contentmappertest.NewSpawner().Spawn(os.Args[1:], "", os.Stderr)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	defer connection.Close()
	input := make(chan error, 1)
	output := make(chan error, 1)
	go func() {
		_, err := io.Copy(connection, os.Stdin)
		input <- err
	}()
	go func() {
		_, err := io.Copy(os.Stdout, connection)
		output <- err
	}()
	select {
	case err = <-input:
		connection.Close()
		<-output
	case err = <-output:
	}
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
	}
}
