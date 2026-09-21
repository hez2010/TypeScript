import {
    type Choice as State,
    message,
    type Scalar,
    value,
} from "./values";
export type Result = Scalar | undefined;
export const copy: Scalar = value;
export const status: State = message;
