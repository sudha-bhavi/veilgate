import { useLocation } from "react-router-dom";

/** Shows where the router ended up, including the query string. */
export default function Where({ label }: { label: string }) {
  return <p>{`${label}${useLocation().search}`}</p>;
}
