import ErrorPage from "../components/ErrorPage";

export default function NotFoundPage() {
  return (
    <ErrorPage
      code="404"
      title="Page not found"
      message="This page does not exist, or it has not been built yet."
    />
  );
}
