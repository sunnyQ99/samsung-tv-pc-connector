import java.io.*;
import java.net.URLDecoder;
import java.nio.file.*;
import java.security.*;
import java.security.cert.X509Certificate;
import java.util.*;
import javax.xml.crypto.*;
import javax.xml.crypto.dsig.*;
import javax.xml.crypto.dsig.dom.DOMValidateContext;
import javax.xml.crypto.dsig.keyinfo.*;
import javax.xml.parsers.*;
import org.w3c.dom.*;

/** Offline XMLDSig validation. All detached references are confined to one directory. */
public final class VerifyWgt {
  private static boolean certificatesInDate = true;
  private static final List<String> certificateValidity = new ArrayList<>();
  public static void main(String[] arguments) throws Exception {
    Path root = Paths.get(arguments[0]).toRealPath();
    XMLSignatureFactory factory = XMLSignatureFactory.getInstance("DOM");
    int signatures = 0, references = 0;
    for (String name : Arrays.asList("author-signature.xml", "signature1.xml")) {
      DocumentBuilderFactory builder = DocumentBuilderFactory.newInstance();
      builder.setNamespaceAware(true);
      builder.setFeature("http://apache.org/xml/features/disallow-doctype-decl", true);
      builder.setFeature("http://xml.org/sax/features/external-general-entities", false);
      builder.setFeature("http://xml.org/sax/features/external-parameter-entities", false);
      Document document = builder.newDocumentBuilder().parse(root.resolve(name).toFile());
      Node signatureNode = document.getElementsByTagNameNS(XMLSignature.XMLNS, "Signature").item(0);
      if (signatureNode == null) throw new Exception("Missing XML signature");
      DOMValidateContext context = new DOMValidateContext(new KeySelector() {
        public KeySelectorResult select(KeyInfo keyInfo, Purpose purpose, AlgorithmMethod method, XMLCryptoContext context) throws KeySelectorException {
          for (Object part : keyInfo.getContent()) if (part instanceof X509Data) {
            for (Object value : ((X509Data)part).getContent()) if (value instanceof X509Certificate) {
              final X509Certificate certificate = (X509Certificate)value;
              boolean inDate = true;
              try { certificate.checkValidity(); } catch (Exception error) { inDate = false; certificatesInDate = false; }
              certificateValidity.add("{\"file\":\"" + name + "\",\"inDate\":" + inDate + ",\"notBeforeEpochMs\":" + certificate.getNotBefore().getTime() + ",\"notAfterEpochMs\":" + certificate.getNotAfter().getTime() + "}");
              return () -> certificate.getPublicKey();
            }
          }
          throw new KeySelectorException("No signer certificate");
        }
      }, signatureNode);
      context.setProperty("org.jcp.xml.dsig.secureValidation", Boolean.TRUE);
      NodeList elements = document.getElementsByTagName("*");
      for (int index = 0; index < elements.getLength(); index++) {
        Element element = (Element)elements.item(index);
        if (element.hasAttribute("Id")) context.setIdAttributeNS(element, null, "Id");
      }
      final URIDereferencer standard = factory.getURIDereferencer();
      context.setURIDereferencer((reference, cryptoContext) -> {
        String uri = reference.getURI();
        if (uri == null || uri.isEmpty() || uri.startsWith("#")) return standard.dereference(reference, cryptoContext);
        try {
          String decoded = URLDecoder.decode(uri, "UTF-8");
          if (decoded.contains(":") || decoded.startsWith("/") || decoded.contains("\\")) throw new IOException("Nonlocal signature reference");
          Path target = root.resolve(decoded).normalize().toRealPath();
          if (!target.startsWith(root) || !Files.isRegularFile(target)) throw new IOException("Signature path escape");
          return new OctetStreamData(new ByteArrayInputStream(Files.readAllBytes(target)), uri, null);
        } catch (Exception error) { throw new URIReferenceException("Invalid local signature reference", error); }
      });
      XMLSignature signature = factory.unmarshalXMLSignature(context);
      if (!signature.validate(context)) throw new Exception("XML signature verification failed for " + name);
      references += signature.getSignedInfo().getReferences().size();
      signatures++;
    }
    System.out.println("{\"ok\":true,\"xmlSignaturesValidated\":" + signatures + ",\"referencesValidated\":" + references + ",\"signerCertificatesInDate\":" + certificatesInDate + ",\"signerValidity\":[" + String.join(",", certificateValidity) + "],\"networkAccess\":false,\"tvTrustStoreAcceptanceVerified\":false}");
  }
}
