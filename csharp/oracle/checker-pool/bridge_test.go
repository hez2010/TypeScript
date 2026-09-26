package compiler

import (
	"encoding/json"
	"os"
	"testing"
)

func TestCSharpCheckerPartitions(t *testing.T) {
	var inputs []struct {
		Weights      []int   `json:"weights"`
		Imports      []int   `json:"imports"`
		Declarations []bool  `json:"declarations"`
		Adjacency    [][]int `json:"adjacency"`
		Count        int     `json:"count"`
	}
	input, err := os.ReadFile(os.Getenv("CSHARP_PARTITION_INPUT"))
	if err != nil {
		t.Fatal(err)
	}
	if err := json.Unmarshal(input, &inputs); err != nil {
		t.Fatal(err)
	}
	results := make([][]int, len(inputs))
	for i, row := range inputs {
		total, declarations := 0, 0
		for j, weight := range row.Weights {
			total += weight
			if row.Declarations[j] {
				declarations += weight
			}
		}
		policy := getCheckerAssociationPolicy(total, declarations, row.Count)
		for j := range row.Weights {
			if !row.Declarations[j] {
				row.Weights[j] *= policy.sourceFileWeightMultiplier
			}
		}
		weights := getCheckerAssociationWeights(row.Weights, row.Imports)
		order := getCheckerAssociationOrder(weights, row.Declarations, policy.prioritizeSourceFiles)
		results[i] = getCheckerAssociationsInOrder(weights, row.Adjacency, order, row.Count, policy.balancePenaltyMultiplier)
		if results[i] == nil {
			results[i] = []int{}
		}
	}
	output, err := json.Marshal(results)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(os.Getenv("CSHARP_PARTITION_OUTPUT"), output, 0o600); err != nil {
		t.Fatal(err)
	}
}
